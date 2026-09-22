using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Model;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>A job with two phases of known length — fetch 10 min, persist 20 min — driven on the test
/// clock, for the per-phase timing and duration-metric tests.</summary>
public sealed class PhasedJob : ICheckpointableJob
{
    public const string JobName = "phased";

    public static readonly TimeSpan FetchFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PersistFor = TimeSpan.FromMinutes(20);

    private readonly TestTimeProvider _time;

    public PhasedJob(TimeProvider time) => _time = (TestTimeProvider)time;

    public string Name => JobName;

    public JobCadence Cadence => JobCadence.None;

    public async Task RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync("fetch", 0, 100, cancellationToken).ConfigureAwait(false);
        _time.Advance(FetchFor);
        await context.ReportProgressAsync("persist", 50, 100, cancellationToken).ConfigureAwait(false);
        _time.Advance(PersistFor);
        await context.ReportProgressAsync("persist", 100, 100, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A job whose body throws — for the failure-counter test.</summary>
public sealed class FailingJob : ICheckpointableJob
{
    public const string JobName = "failing";

    public string Name => JobName;

    public JobCadence Cadence => JobCadence.None;

    public Task RunAsync(IJobContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("boom");
}
