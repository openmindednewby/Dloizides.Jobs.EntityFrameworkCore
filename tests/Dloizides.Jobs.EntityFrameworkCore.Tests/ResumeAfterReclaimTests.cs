using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Dloizides.Jobs.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// The headline guarantee: a job that checkpoints, whose owner then DIES (lease lapses), is reclaimed by
/// another runner and CONTINUES FROM THE CHECKPOINT — not from scratch. This is the exact regression that
/// ran a 10h ingest twice on 2026-08-13.
/// </summary>
public sealed class ResumeAfterReclaimTests
{
    [Fact]
    public async Task ReclaimedRun_ResumesFromCheckpoint_NotFromScratch()
    {
        using var harness = TestHarness.Create();

        // Trigger the ingest.
        var trigger = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobTrigger>().TriggerAsync(
                ResumableIngestJob.JobName, JobTriggerSources.Manual, "tester", null, null, default));
        trigger.Status.ShouldBe(JobTriggerStatus.Accepted);
        var runId = trigger.Run!.Id;

        // Owner A claims and runs it, checkpoints at cursor 5, then its pod "dies" (cancels its token). The
        // runner must LEAVE it running for reclaim — never mark it failed and discard the progress.
        var runnerA = harness.NewRunner();
        await runnerA.RunOnceAsync(harness.Probe.FirstRunCts.Token);

        harness.Probe.ResumableInvocations.ShouldBe(1);
        harness.Probe.ResumedFromCursor.ShouldBeNull();
        var afterA = await harness.StoreAsync(s => s.GetRunAsync(runId, default));
        afterA!.Outcome.ShouldBe(JobRunOutcomes.Running);
        afterA.Checkpoint.ShouldNotBeNull();
        afterA.StartedAt.ShouldNotBeNull();

        // A's lease lapses (its pod is gone).
        harness.Time.Advance(TimeSpan.FromSeconds(121));

        // Owner B reclaims and RESUMES from the checkpoint.
        var runnerB = harness.NewRunner();
        await runnerB.RunOnceAsync(CancellationToken.None);

        harness.Probe.ResumableInvocations.ShouldBe(2);
        harness.Probe.ResumedFromCursor.ShouldBe(5); // proof: it continued from the checkpoint, not from 0
        harness.Probe.ResumableCompleted.ShouldBeTrue();

        var afterB = await harness.StoreAsync(s => s.GetRunAsync(runId, default));
        afterB!.Outcome.ShouldBe(JobRunOutcomes.Completed);
        afterB.StartedAt.ShouldBe(afterA.StartedAt); // StartedAt preserved across the reclaim
        afterB.ClaimedBy.ShouldBeNull(); // lease released on completion
    }

    [Fact]
    public async Task ReclaimedRun_ReloadsEarlierPhasesFromProgress_AndCountsDowntimeInTheOpenPhase()
    {
        var t0 = DateTimeOffset.Parse("2026-09-22T10:00:00Z");
        using var harness = TestHarness.Create(start: t0, extraJobs: jobs => jobs.AddJob<PhasedJob>());
        var runStart = t0.AddHours(-1);
        var fetchStart = t0.AddMinutes(-30);

        // A dead owner left the run mid-"fetch": an earlier "load" phase closed, "fetch" open, lease lapsed.
        await harness.SeedAsync(new JobRun
        {
            JobName = PhasedJob.JobName,
            TriggerSource = JobTriggerSources.Manual,
            TriggeredBy = "tester",
            TriggeredAt = runStart,
            StartedAt = runStart,
            Outcome = JobRunOutcomes.Running,
            ClaimedBy = "dead-pod",
            LeaseExpiresAt = t0.AddMinutes(-5),
            Progress = JobJson.Serialize(new ProgressSnapshot("fetch", 10, 100, fetchStart)
            {
                Phases = new[]
                {
                    new JobPhaseSpan("load", runStart, fetchStart),
                    new JobPhaseSpan("fetch", fetchStart),
                },
            }),
        });

        (await harness.NewRunner().RunOnceAsync(CancellationToken.None)).ShouldBeTrue();

        var status = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobStatusQuery>().GetAsync(PhasedJob.JobName, default));
        status.ShouldNotBeNull();
        status.Phases.Select(p => p.Phase).ShouldBe(new[] { "load", "fetch", "persist" });
        status.Phases[0].Duration.ShouldBe(TimeSpan.FromMinutes(30));
        status.Phases[1].StartedAt.ShouldBe(fetchStart);
        // The 30 min the run sat unowned count inside the open "fetch" phase, plus the 10 min it then ran.
        status.Phases[1].Duration.ShouldBe(TimeSpan.FromMinutes(30) + PhasedJob.FetchFor);
        status.Phases[2].Duration.ShouldBe(PhasedJob.PersistFor);
    }
}
