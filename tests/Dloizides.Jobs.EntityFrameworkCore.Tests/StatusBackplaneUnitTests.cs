using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Backplane;
using Dloizides.Jobs.Configuration;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Extensions;
using Dloizides.Jobs.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// Transport-level unit tests for the status backplane seam: the Null default is inert, an in-memory
/// backplane fans an event from one instance out to a subscriber on ANOTHER (two pods on one transport), and
/// the config-driven resolver picks the selected impl (unknown → Null).
/// </summary>
public sealed class StatusBackplaneUnitTests
{
    [Fact]
    public async Task NullBackplane_PublishIsNoOp_AndSubscribeHandleDisposesCleanly()
    {
        var backplane = NullJobStatusBackplane.Instance;
        var received = 0;

        using (var subscription = backplane.Subscribe(_ => received++))
        {
            // A no-op transport must accept a publish without throwing and without delivering anything.
            await backplane.PublishAsync(
                new JobStatusEvent("job", JobStatusEventKinds.Progress, Guid.NewGuid(), DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        received.ShouldBe(0);
    }

    [Fact]
    public async Task InMemoryBackplane_DeliversEventPublishedOnOneInstance_ToSubscriberOnAnother()
    {
        // One shared transport, two backplane instances over it = two pods. This is the cross-instance
        // fan-out guarantee, proven deterministically without a broker.
        var transport = new InMemoryJobStatusBus();
        var podA = new InMemoryJobStatusBackplane(transport);
        var podB = new InMemoryJobStatusBackplane(transport);

        JobStatusEvent? seenOnA = null;
        using var subscription = podA.Subscribe(evt => seenOnA = evt);

        var published = new JobStatusEvent(
            "ingest", JobStatusEventKinds.Progress, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await podB.PublishAsync(published, CancellationToken.None);

        seenOnA.ShouldNotBeNull();
        seenOnA.Job.ShouldBe("ingest");
        seenOnA.Kind.ShouldBe(JobStatusEventKinds.Progress);
        seenOnA.RunId.ShouldBe(published.RunId);
    }

    [Fact]
    public async Task InMemoryBackplane_DisposedSubscription_StopsReceiving()
    {
        var transport = new InMemoryJobStatusBus();
        var backplane = new InMemoryJobStatusBackplane(transport);
        var count = 0;

        var subscription = backplane.Subscribe(_ => count++);
        await backplane.PublishAsync(Evt(), CancellationToken.None);
        subscription.Dispose();
        await backplane.PublishAsync(Evt(), CancellationToken.None);

        count.ShouldBe(1); // only the pre-dispose publish landed

        static JobStatusEvent Evt() =>
            new("j", JobStatusEventKinds.Claimed, Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Resolver_DefaultsToNull_WhenNoBackplaneSelected()
    {
        using var provider = BuildProvider(configureJobs: null, backplane: null);

        provider.GetRequiredService<IJobStatusBackplane>().ShouldBeOfType<NullJobStatusBackplane>();
    }

    [Fact]
    public void Resolver_PicksInMemory_WhenSelected()
    {
        using var provider = BuildProvider(
            configureJobs: jobs => jobs.UseInMemoryStatusBackplane(),
            backplane: JobStatusBackplanes.InMemory);

        provider.GetRequiredService<IJobStatusBackplane>().ShouldBeOfType<InMemoryJobStatusBackplane>();
    }

    [Fact]
    public void Resolver_FallsBackToNull_WhenSelectedKeyIsUnknown()
    {
        using var provider = BuildProvider(
            configureJobs: jobs => jobs.UseInMemoryStatusBackplane(),
            backplane: "SomeTransportNobodyRegistered");

        // An unrecognised value must never fail startup — push is opt-in and never load-bearing.
        provider.GetRequiredService<IJobStatusBackplane>().ShouldBeOfType<NullJobStatusBackplane>();
    }

    private static ServiceProvider BuildProvider(Action<JobsBuilder>? configureJobs, string? backplane)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestJobsDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddDloizidesJobs(jobs =>
        {
            jobs.AddJob<SimpleJob>();
            jobs.UseEntityFrameworkStore<TestJobsDbContext>();
            configureJobs?.Invoke(jobs);
            if (backplane is not null)
            {
                jobs.Configure(o => o.Status.Backplane = backplane);
            }
        });
        return services.BuildServiceProvider();
    }
}
