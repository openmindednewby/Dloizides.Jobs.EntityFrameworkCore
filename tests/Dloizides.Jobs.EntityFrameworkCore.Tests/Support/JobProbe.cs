namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Shared observable state for the probe jobs (registered as a singleton). Jobs are resolved fresh per run,
/// so they record what they saw here for the test to assert on afterwards.
/// </summary>
public sealed class JobProbe
{
    /// <summary>The token the FIRST run of the resumable job cancels to simulate its pod being killed
    /// mid-run (SIGTERM). The test passes this token to the first runner.</summary>
    public CancellationTokenSource FirstRunCts { get; } = new();

    /// <summary>How many times the resumable job body has been entered.</summary>
    public int ResumableInvocations { get; set; }

    /// <summary>The cursor the resumed run loaded from its checkpoint — the proof it resumed rather than
    /// restarted. Null until the second (resuming) invocation.</summary>
    public int? ResumedFromCursor { get; set; }

    /// <summary>Set when the resumable job reached its completing branch.</summary>
    public bool ResumableCompleted { get; set; }

    /// <summary>Set when the checkpointing demo job ran.</summary>
    public bool CheckpointingRan { get; set; }
}

/// <summary>The resumable ingest job's opaque checkpoint state.</summary>
public sealed record IngestState(int Cursor);

/// <summary>The demo job's typed checkpoint state, used to prove a round-trip through jsonb.</summary>
public sealed record DemoState(string Phase, int Done);
