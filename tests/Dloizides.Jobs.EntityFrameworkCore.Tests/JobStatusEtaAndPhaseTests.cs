using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Dloizides.Jobs.Runtime;
using Dloizides.Jobs.Services;
using Dloizides.Jobs.Status;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// JOBS-VIS-1 "Make job statuses visible", AC-J3 (server-side ETA) + AC-J4 (per-phase durations in
/// <c>JobStatus</c>).
/// </summary>
public sealed class JobStatusEtaAndPhaseTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-22T10:00:00Z");

    [Fact]
    public void Estimate_WhenQuarterDoneAfterOneHour_ProjectsThreeMoreHours() =>
        JobEta.Estimate(25, 100, T0, T0.AddHours(1)).ShouldBe(T0.AddHours(4));

    [Theory]
    [InlineData(25, 0)]    // no total
    [InlineData(0, 100)]   // nothing done yet
    [InlineData(100, 100)] // already done
    public void Estimate_WhenItWouldBeAGuess_ReturnsNull(long done, long total) =>
        JobEta.Estimate(done, total, T0, T0.AddHours(1)).ShouldBeNull();

    [Fact]
    public void Estimate_WhenLessThanFiveMinutesObserved_ReturnsNull() =>
        JobEta.Estimate(25, 100, T0, T0.AddMinutes(4)).ShouldBeNull();

    [Fact]
    public void Estimate_WhenStartUnknown_ReturnsNull() =>
        JobEta.Estimate(25, 100, null, T0.AddHours(1)).ShouldBeNull();

    [Fact]
    public async Task Status_WhenRunningWithProgress_CarriesServerEta()
    {
        // AC-J3
        using var harness = TestHarness.Create(start: T0);
        await SeedRunningAsync(harness, done: 25, total: 100, startedAt: T0.AddHours(-1));

        var status = await GetStatusAsync(harness, SimpleJob.JobName);

        status.EstimatedCompletion.ShouldBe(T0.AddHours(3));
    }

    [Fact]
    public async Task Status_WhenRunningWithoutTotal_HasNoEta()
    {
        // AC-J3
        using var harness = TestHarness.Create(start: T0);
        await SeedRunningAsync(harness, done: 25, total: 0, startedAt: T0.AddHours(-1));

        var status = await GetStatusAsync(harness, SimpleJob.JobName);

        status.EstimatedCompletion.ShouldBeNull();
    }

    [Fact]
    public async Task Status_AfterAPhasedRun_ExposesEachPhaseDurationAndTimelineEvents()
    {
        // AC-J4
        using var harness = TestHarness.Create(start: T0, extraJobs: jobs => jobs.AddJob<PhasedJob>());
        await harness.InScopeAsync(sp => sp.GetRequiredService<IJobTrigger>().TriggerAsync(
            PhasedJob.JobName, JobTriggerSources.Manual, "tester", null, null, default));
        await harness.NewRunner().RunOnceAsync(CancellationToken.None);

        var status = await GetStatusAsync(harness, PhasedJob.JobName);

        status.Phases.Select(p => p.Phase).ShouldBe(new[] { "fetch", "persist" });
        status.Phases[0].Duration.ShouldBe(PhasedJob.FetchFor);
        status.Phases[1].Duration.ShouldBe(PhasedJob.PersistFor);
        status.Phases[1].EndedAt.ShouldBe(T0 + PhasedJob.FetchFor + PhasedJob.PersistFor);
        status.EstimatedCompletion.ShouldBeNull(); // finished: nothing left to estimate
        status.Timeline.ShouldContain(e =>
            e.Event == "phase-started" && e.Phase == "persist" && e.At == T0 + PhasedJob.FetchFor);
        status.Timeline.ShouldContain(e =>
            e.Event == "phase-ended" && e.Phase == "fetch" && e.At == T0 + PhasedJob.FetchFor);
    }

    private static async Task<JobStatus> GetStatusAsync(TestHarness harness, string job)
    {
        var status = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobStatusQuery>().GetAsync(job, default));
        status.ShouldNotBeNull();
        return status;
    }

    private static Task SeedRunningAsync(TestHarness harness, long done, long total, DateTimeOffset startedAt)
    {
        var now = harness.Time.GetUtcNow();
        return harness.SeedAsync(new JobRun
        {
            JobName = SimpleJob.JobName,
            TriggerSource = JobTriggerSources.Manual,
            TriggeredBy = "tester",
            TriggeredAt = startedAt,
            StartedAt = startedAt,
            Outcome = JobRunOutcomes.Running,
            ClaimedBy = "other-pod",
            LeaseExpiresAt = now.AddHours(1),
            Progress = JobJson.Serialize(new ProgressSnapshot("persist", done, total, now)),
        });
    }
}
