using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Configuration;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Metrics;
using Dloizides.Jobs.Model;
using Dloizides.Jobs.Services;
using Dloizides.Jobs.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// JOBS-VIS-1 "Make job statuses visible", AC-J1 + AC-J2: the <c>Dloizides.Jobs</c> meter reports what a
/// dashboard needs to tell "slow" from "stuck", observed with a MeterListener exactly as an exporter would.
/// </summary>
public sealed class JobMetricsTests
{
    [Fact]
    public async Task CompletedRun_WhenObserved_ReportsLastSuccessDurationAndFullProgress()
    {
        // AC-J1
        var service = NewServiceName();
        using var capture = new MeterCapture(service);
        using var harness = CreateHarness(service);
        var startedAt = harness.Time.GetUtcNow();

        await TriggerAndRunAsync(harness, PhasedJob.JobName);

        var completedAt = startedAt + PhasedJob.FetchFor + PhasedJob.PersistFor;
        capture.Latest(JobMetricNames.LastSuccessTimestampSeconds, PhasedJob.JobName)
            .ShouldBe(completedAt.ToUnixTimeSeconds());
        capture.All(JobMetricNames.RunDurationSeconds, PhasedJob.JobName)
            .ShouldBe(new[] { (PhasedJob.FetchFor + PhasedJob.PersistFor).TotalSeconds });
        capture.Latest(JobMetricNames.ProgressRatio, PhasedJob.JobName).ShouldBe(1d);
        capture.Latest(JobMetricNames.Running, PhasedJob.JobName).ShouldBe(0d);
        capture.All(JobMetricNames.FailuresTotal, PhasedJob.JobName).ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedRun_WhenObserved_CountsOneFailureAndNoLastSuccess()
    {
        var service = NewServiceName();
        using var capture = new MeterCapture(service);
        using var harness = CreateHarness(service);

        await TriggerAndRunAsync(harness, FailingJob.JobName);

        capture.All(JobMetricNames.FailuresTotal, FailingJob.JobName).Sum().ShouldBe(1d);
        capture.Latest(JobMetricNames.LastSuccessTimestampSeconds, FailingJob.JobName).ShouldBeNull();
        capture.Latest(JobMetricNames.Running, FailingJob.JobName).ShouldBe(0d);
    }

    [Fact]
    public async Task StaleWatchedJob_WhenItRecovers_SetsStaleToOneThenBackToZero()
    {
        // AC-J2
        var service = NewServiceName();
        using var capture = new MeterCapture(service);
        using var harness = CreateHarness(service);
        var monitor = harness.Provider.GetRequiredService<IJobStalenessMonitor>();

        await SeedCompletedWatchedRunAsync(harness);
        harness.Time.Advance(WatchedJob.StaleAfter + TimeSpan.FromMinutes(30));
        await monitor.CheckOnceAsync(default);
        capture.Latest(JobMetricNames.Stale, WatchedJob.JobName).ShouldBe(1d);

        await SeedCompletedWatchedRunAsync(harness); // the job recovers: a fresh success
        await monitor.CheckOnceAsync(default);
        capture.Latest(JobMetricNames.Stale, WatchedJob.JobName).ShouldBe(0d);
    }

    [Fact]
    public async Task LoggingAlarm_WhenRaised_KeepsItsLogLineAndSetsStale()
    {
        // AC-J2, step 4: the default alarm also sets jobs_stale, not only the watchdog.
        var service = NewServiceName();
        using var capture = new MeterCapture(service);
        using var metrics = new JobMetrics(Options.Create(new JobsOptions { ServiceName = service }));
        var alarm = new LoggingJobStalenessAlarm(NullLogger<LoggingJobStalenessAlarm>.Instance, metrics);

        await alarm.RaiseAsync(
            new StaleJob("nightly", null, TimeSpan.FromHours(1), TimeSpan.FromHours(2)), default);

        capture.Latest(JobMetricNames.Stale, "nightly").ShouldBe(1d);
    }

    private static string NewServiceName() => $"svc-{Guid.NewGuid():N}";

    private static TestHarness CreateHarness(string service) =>
        TestHarness.Create(extraJobs: jobs =>
        {
            jobs.AddJob<PhasedJob>();
            jobs.AddJob<FailingJob>();
            jobs.Configure(o => o.ServiceName = service);
        });

    private static async Task TriggerAndRunAsync(TestHarness harness, string jobName)
    {
        var trigger = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobTrigger>().TriggerAsync(
                jobName, JobTriggerSources.Manual, "tester", null, null, default));
        trigger.Status.ShouldBe(JobTriggerStatus.Accepted);
        (await harness.NewRunner().RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
    }

    private static Task SeedCompletedWatchedRunAsync(TestHarness harness)
    {
        var at = harness.Time.GetUtcNow();
        return harness.SeedAsync(new JobRun
        {
            JobName = WatchedJob.JobName,
            TriggerSource = JobTriggerSources.Scheduled,
            TriggeredBy = JobTriggerSources.SystemActor,
            TriggeredAt = at,
            StartedAt = at,
            CompletedAt = at,
            Outcome = JobRunOutcomes.Completed,
        });
    }
}
