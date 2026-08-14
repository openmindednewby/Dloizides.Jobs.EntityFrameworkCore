using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Dloizides.Jobs.Status;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// Proves PERSIST FIRST, PUSH SECOND against the REAL EF store, and that a publish is wired at every
/// persistence point. A recording backplane, for each event it receives, re-reads the durable row in a fresh
/// scope and asserts the change is ALREADY there — so the store write demonstrably preceded the push. It also
/// captures the full sequence of kinds a single run emits (queued → claimed → progress → checkpoint →
/// completed), which is the coverage that the publish call sits at each of enqueue/claim/progress/checkpoint/
/// complete.
/// </summary>
public sealed class PersistThenPushTests
{
    [Fact]
    public async Task EveryPush_FindsTheStoreAlreadyReflectingTheChange_AndFiresAtEveryPersistencePoint()
    {
        using var harness = TestHarness.Create(extraJobs: jobs =>
        {
            jobs.AddStatusBackplane(
                "Recording", sp => new RecordingBackplane(sp.GetRequiredService<IServiceScopeFactory>()));
            jobs.Configure(o => o.Status.Backplane = "Recording");
        });

        // Trigger (→ queued push) then run once (→ claimed, progress, checkpoint, completed pushes).
        var trigger = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobTrigger>().TriggerAsync(
                CheckpointingJob.JobName, JobTriggerSources.Manual, "tester", null, null, default));
        trigger.Status.ShouldBe(JobTriggerStatus.Accepted);

        var runner = harness.NewRunner();
        await runner.RunOnceAsync(CancellationToken.None);

        harness.Probe.CheckpointingRan.ShouldBeTrue();

        var recording = (RecordingBackplane)harness.Provider.GetRequiredService<IJobStatusBackplane>();
        var pushes = recording.Pushes.ToList();

        // Persist-first-push-second: not one push observed the store lagging behind it.
        pushes.ShouldNotBeEmpty();
        pushes.ShouldAllBe(p => p.StoreAlreadyReflectsChange);

        // A publish fired at each persistence point across the run's lifecycle.
        var kinds = pushes.Select(p => p.Kind).ToList();
        kinds.ShouldContain(JobStatusEventKinds.Queued);
        kinds.ShouldContain(JobStatusEventKinds.Claimed);
        kinds.ShouldContain(JobStatusEventKinds.Progress);
        kinds.ShouldContain(JobStatusEventKinds.Checkpoint);
        kinds.ShouldContain(JobRunOutcomes.Completed);
    }

    [Fact]
    public async Task ReclaimedRun_PublishesReclaimed_AfterTheResumingClaimPersists()
    {
        using var harness = TestHarness.Create(extraJobs: jobs =>
        {
            jobs.AddStatusBackplane(
                "Recording", sp => new RecordingBackplane(sp.GetRequiredService<IServiceScopeFactory>()));
            jobs.Configure(o => o.Status.Backplane = "Recording");
        });

        await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobTrigger>().TriggerAsync(
                ResumableIngestJob.JobName, JobTriggerSources.Manual, "tester", null, null, default));

        // Owner A claims, checkpoints, then "dies"; owner B reclaims after the lease lapses and resumes.
        await harness.NewRunner().RunOnceAsync(harness.Probe.FirstRunCts.Token);
        harness.Time.Advance(TimeSpan.FromSeconds(121));
        await harness.NewRunner().RunOnceAsync(CancellationToken.None);

        harness.Probe.ResumedFromCursor.ShouldBe(5);

        var recording = (RecordingBackplane)harness.Provider.GetRequiredService<IJobStatusBackplane>();
        var pushes = recording.Pushes.ToList();

        pushes.ShouldAllBe(p => p.StoreAlreadyReflectsChange);
        pushes.Select(p => p.Kind).ShouldContain(JobStatusEventKinds.Reclaimed);
    }
}
