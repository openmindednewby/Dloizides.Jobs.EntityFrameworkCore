using Dloizides.Jobs.Configuration;
using Dloizides.Jobs.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// Cross-instance fan-out over REAL Postgres <c>LISTEN</c>/<c>NOTIFY</c>. Gated on the
/// <c>JOBS_PG_TEST_CONN</c> environment variable (a connection string to a throwaway Postgres): the test host
/// here has no Postgres, so absent that variable the test is a no-op — the in-memory cross-instance test
/// (<see cref="StatusBackplaneUnitTests"/>) carries the deterministic fan-out guarantee, and this one is the
/// real-transport confirmation for a host that has a database.
/// </summary>
[Trait("Category", "PostgresIntegration")]
public sealed class PostgresListenNotifyBackplaneTests
{
    private const string ConnectionEnvVar = "JOBS_PG_TEST_CONN";

    [Fact]
    public async Task EventPublishedOnOnePod_ReachesASubscriberOnAnother_OverListenNotify()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No Postgres in this host — see the class summary. Deliberately a green no-op, not a silent gap:
            // the deterministic cross-instance guarantee is proven by the in-memory test.
            return;
        }

        var options = Options.Create(new JobsOptions
        {
            Status = { Backplane = JobStatusBackplanes.Postgres, Channel = "dloizides_jobs_test" },
        });

        using var podA = new PostgresListenNotifyBackplane(
            () => connectionString!, options, channelOverride: null, NullLogger<PostgresListenNotifyBackplane>.Instance);
        using var podB = new PostgresListenNotifyBackplane(
            () => connectionString!, options, channelOverride: null, NullLogger<PostgresListenNotifyBackplane>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ((IHostedService)podA).StartAsync(cts.Token);
        await ((IHostedService)podB).StartAsync(cts.Token);

        // Give both LISTEN connections a beat to establish before publishing.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);

        var received = new TaskCompletionSource<JobStatusEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = podA.Subscribe(evt => received.TrySetResult(evt));

        var published = new JobStatusEvent(
            "ingest", JobStatusEventKinds.Progress, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await podB.PublishAsync(published, cts.Token);

        var delivered = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
        delivered.ShouldBe(received.Task, "the NOTIFY from pod B should have reached pod A's LISTEN connection");

        var evt = await received.Task;
        evt.RunId.ShouldBe(published.RunId);
        evt.Job.ShouldBe("ingest");

        await ((IHostedService)podA).StopAsync(CancellationToken.None);
        await ((IHostedService)podB).StopAsync(CancellationToken.None);
    }
}
