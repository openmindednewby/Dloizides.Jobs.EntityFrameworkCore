using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Model;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>
/// The resumable ingest: on a FRESH run it processes to cursor 5, checkpoints, then simulates its pod dying
/// mid-run (cancels its token and throws). On a RESUMED run it loads the checkpoint and finishes from there.
/// This is the behaviour the resume-after-reclaim test drives end to end.
/// </summary>
public sealed class ResumableIngestJob : ICheckpointableJob
{
    /// <summary>The single-flight name.</summary>
    public const string JobName = "resumable-ingest";

    private readonly JobProbe _probe;

    public ResumableIngestJob(JobProbe probe) => _probe = probe;

    public string Name => JobName;

    public JobCadence Cadence => JobCadence.None;

    public async Task RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        _probe.ResumableInvocations++;
        var state = context.LoadCheckpoint<IngestState>();

        if (state is null)
        {
            // Fresh run: make some progress, checkpoint it, then simulate SIGTERM. The runner must LEAVE the
            // run 'running' (not fail it) so its lease lapses and another runner resumes from this checkpoint.
            await context.ReportProgressAsync("ingest", 5, 10, cancellationToken).ConfigureAwait(false);
            await context.SaveCheckpointAsync(new IngestState(5), cancellationToken).ConfigureAwait(false);
            _probe.FirstRunCts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        // Resumed run: continue FROM the checkpoint, not from scratch.
        _probe.ResumedFromCursor = state.Cursor;
        await context.ReportProgressAsync("ingest", 10, 10, cancellationToken).ConfigureAwait(false);
        await context.SaveCheckpointAsync(new IngestState(10), cancellationToken).ConfigureAwait(false);
        _probe.ResumableCompleted = true;
    }
}

/// <summary>A job that checkpoints a typed object and reports progress, then completes — for the
/// checkpoint round-trip + progress-reporting test.</summary>
public sealed class CheckpointingJob : ICheckpointableJob
{
    public const string JobName = "checkpointing";

    private readonly JobProbe _probe;

    public CheckpointingJob(JobProbe probe) => _probe = probe;

    public string Name => JobName;

    public JobCadence Cadence => JobCadence.None;

    public async Task RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync("importing", 45, 100, cancellationToken).ConfigureAwait(false);
        await context.SaveCheckpointAsync(new DemoState("importing", 45), cancellationToken).ConfigureAwait(false);
        _probe.CheckpointingRan = true;
    }
}

/// <summary>A trivial job that completes immediately — for trigger and single-flight tests.</summary>
public sealed class SimpleJob : ICheckpointableJob
{
    public const string JobName = "simple";

    public string Name => JobName;

    public JobCadence Cadence => JobCadence.None;

    public Task RunAsync(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A WATCHED job (expects a success hourly, stale after two hours) — for the watchdog test. Its
/// body is irrelevant; the test seeds run rows and advances the clock.</summary>
public sealed class WatchedJob : ICheckpointableJob
{
    public const string JobName = "watched";

    public static readonly TimeSpan Every = TimeSpan.FromHours(1);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    public string Name => JobName;

    public JobCadence Cadence => JobCadence.Every(Every, StaleAfter);

    public Task RunAsync(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
