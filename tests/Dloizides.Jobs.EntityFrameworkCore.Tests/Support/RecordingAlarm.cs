using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Status;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>An <see cref="IJobStalenessAlarm"/> that records what it was asked to raise, so the watchdog
/// test can assert the alarm actually fired (not just that the monitor returned a list).</summary>
public sealed class RecordingAlarm : IJobStalenessAlarm
{
    private readonly List<StaleJob> _raised = new();

    public IReadOnlyList<StaleJob> Raised
    {
        get
        {
            lock (_raised)
            {
                return _raised.ToList();
            }
        }
    }

    public Task RaiseAsync(StaleJob job, CancellationToken cancellationToken)
    {
        lock (_raised)
        {
            _raised.Add(job);
        }

        return Task.CompletedTask;
    }
}
