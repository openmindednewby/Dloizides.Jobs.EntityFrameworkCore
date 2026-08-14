using System.Collections.Concurrent;
using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Backplane;
using Dloizides.Jobs.Model;
using Dloizides.Jobs.Status;
using Microsoft.Extensions.DependencyInjection;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>
/// A backplane that, for every event PUBLISHED to it, opens a fresh store scope and records whether the
/// store ALREADY reflects the change — the direct probe for persist-first-push-second. Because the runtime
/// publishes only after the store write commits, every recorded push should find the change already durable.
/// It also fans out locally (so it can double as a normal in-process backplane in a test).
/// </summary>
public sealed class RecordingBackplane : IJobStatusBackplane
{
    private readonly IServiceScopeFactory _scopes;
    private readonly InMemoryJobStatusBus _bus = new();

    public RecordingBackplane(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>Every push, in order, with whether the durable store already showed the change at push time.</summary>
    public ConcurrentQueue<RecordedPush> Pushes { get; } = new();

    public async Task PublishAsync(JobStatusEvent evt, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var run = await store.GetRunAsync(evt.RunId, cancellationToken).ConfigureAwait(false);

        var reflects = evt.Kind switch
        {
            JobStatusEventKinds.Progress => run?.Progress is not null,
            JobStatusEventKinds.Checkpoint => run?.Checkpoint is not null,
            JobStatusEventKinds.Queued => run is not null && JobRunOutcomes.OccupiesSlot(run.Outcome),
            JobStatusEventKinds.Claimed or JobStatusEventKinds.Reclaimed =>
                run?.Outcome == JobRunOutcomes.Running,
            _ => run is not null && JobRunOutcomes.IsTerminal(run.Outcome),
        };

        Pushes.Enqueue(new RecordedPush(evt.Kind, reflects));
        _bus.Publish(evt);
    }

    public IDisposable Subscribe(Action<JobStatusEvent> handler) => _bus.Subscribe(handler);
}

/// <summary>One recorded push: the event kind and whether the durable store already reflected it.</summary>
/// <param name="Kind">The <see cref="JobStatusEventKinds"/> value (or terminal outcome).</param>
/// <param name="StoreAlreadyReflectsChange">True when the store showed the change before the push — the
/// persist-first-push-second invariant.</param>
public sealed record RecordedPush(string Kind, bool StoreAlreadyReflectsChange);
