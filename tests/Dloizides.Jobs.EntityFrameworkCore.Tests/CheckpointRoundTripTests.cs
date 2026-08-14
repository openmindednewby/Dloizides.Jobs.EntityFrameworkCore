using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Dloizides.Jobs.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// Checkpoints round-trip through the jsonb column and progress lands in the §8 status shape: phase, done,
/// total and the derived percentage the console renders.
/// </summary>
public sealed class CheckpointRoundTripTests
{
    [Fact]
    public async Task Checkpoint_RoundTrips_ThroughJsonbAndLoadCheckpoint()
    {
        using var harness = TestHarness.Create();
        var runId = await RunCheckpointingJobAsync(harness);

        var run = await harness.StoreAsync(s => s.GetRunAsync(runId, default));
        run!.Checkpoint.ShouldNotBeNull();

        // Round-trip through the SAME public path a resuming job uses: LoadCheckpoint<T> off a JobContext
        // built from the stored checkpoint string.
        var scopes = harness.Provider.GetRequiredService<IServiceScopeFactory>();
        var time = harness.Provider.GetRequiredService<TimeProvider>();
        var context = new JobContext(scopes, runId, "reader", run.Argument, run.Checkpoint, time);

        var loaded = context.LoadCheckpoint<DemoState>();
        loaded.ShouldBe(new DemoState("importing", 45));
    }

    [Fact]
    public async Task Progress_SurfacesInStatus_WithDerivedPercentage()
    {
        using var harness = TestHarness.Create();
        await RunCheckpointingJobAsync(harness);

        var status = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobStatusQuery>().GetAsync(CheckpointingJob.JobName, default));

        status.ShouldNotBeNull();
        status.State.ShouldBe(JobRunOutcomes.Completed);
        status.Progress.ShouldNotBeNull();
        status.Progress.Phase.ShouldBe("importing");
        status.Progress.Done.ShouldBe(45);
        status.Progress.Total.ShouldBe(100);
        status.Progress.Pct.ShouldBe(45); // derived done*100/total
        status.Checkpoint.ShouldNotBeNull();
        status.Checkpoint.ResumableFrom.ShouldBe("importing");
        status.Timeline.ShouldContain(e => e.Event == "checkpoint");
        status.Timeline.ShouldContain(e => e.Event == JobRunOutcomes.Completed);
    }

    private static async Task<Guid> RunCheckpointingJobAsync(TestHarness harness)
    {
        var trigger = await harness.InScopeAsync(sp =>
            sp.GetRequiredService<IJobTrigger>().TriggerAsync(
                CheckpointingJob.JobName, JobTriggerSources.Manual, "tester", null, null, default));
        trigger.Status.ShouldBe(JobTriggerStatus.Accepted);

        var runner = harness.NewRunner();
        await runner.RunOnceAsync(CancellationToken.None);
        harness.Probe.CheckpointingRan.ShouldBeTrue();

        return trigger.Run!.Id;
    }
}
