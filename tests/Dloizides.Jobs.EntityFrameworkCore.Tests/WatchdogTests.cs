using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// The watchdog: a job whose last success has aged past its cadence threshold raises an alarm — the page
/// that was missing on 2026-08-13. And a job still within its threshold does NOT alarm (no false pages).
/// </summary>
public sealed class WatchdogTests
{
    [Fact]
    public async Task JobPastItsStalenessThreshold_RaisesTheAlarm()
    {
        var alarm = new RecordingAlarm();
        using var harness = TestHarness.Create(preConfigure: services =>
        {
            services.AddSingleton(alarm);
            services.AddSingleton<IJobStalenessAlarm>(sp => sp.GetRequiredService<RecordingAlarm>());
        });

        await SeedCompletedWatchedRunAsync(harness);
        harness.Time.Advance(WatchedJob.StaleAfter + TimeSpan.FromMinutes(30)); // now overdue

        var monitor = harness.Provider.GetRequiredService<IJobStalenessMonitor>();
        var stale = await monitor.CheckOnceAsync(default);

        stale.ShouldContain(j => j.Job == WatchedJob.JobName);
        alarm.Raised.ShouldContain(j => j.Job == WatchedJob.JobName);
    }

    [Fact]
    public async Task JobWithinItsThreshold_DoesNotAlarm()
    {
        var alarm = new RecordingAlarm();
        using var harness = TestHarness.Create(preConfigure: services =>
        {
            services.AddSingleton(alarm);
            services.AddSingleton<IJobStalenessAlarm>(sp => sp.GetRequiredService<RecordingAlarm>());
        });

        await SeedCompletedWatchedRunAsync(harness);
        harness.Time.Advance(WatchedJob.Every); // one cadence, still under the stale threshold

        var monitor = harness.Provider.GetRequiredService<IJobStalenessMonitor>();
        var stale = await monitor.CheckOnceAsync(default);

        stale.ShouldBeEmpty();
        alarm.Raised.ShouldBeEmpty();
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
