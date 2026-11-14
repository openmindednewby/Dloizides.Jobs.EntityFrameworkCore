using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

public sealed class EfCore10RuntimeTests
{
    private const int EfCore10Major = 10;
    private const string Owner = "pod-a";

    [Fact]
    public void EfCoreRuntime_WithTestProjectReferences_IsMajorTen()
    {
        var efAssembly = typeof(DbContext).Assembly;

        var major = efAssembly.GetName().Version!.Major;

        major.ShouldBe(EfCore10Major);
    }

    [Fact]
    public async Task HeartbeatAsync_WithEfCore10Runtime_ExtendsTheLease()
    {
        using var harness = TestHarness.Create();
        var now = harness.Time.GetUtcNow();
        var run = new JobRun
        {
            JobName = SimpleJob.JobName,
            TriggerSource = JobTriggerSources.Manual,
            TriggeredBy = "tester",
            TriggeredAt = now,
            Outcome = JobRunOutcomes.Queued,
        };
        var enqueued = await harness.StoreAsync(s => s.EnqueueAsync(run, default));
        await harness.StoreAsync(s => s.ClaimNextAsync(Owner, now, now + TimeSpan.FromSeconds(120), default));
        var extendedLease = now + TimeSpan.FromSeconds(300);

        var extended = await harness.StoreAsync(s => s.HeartbeatAsync(enqueued.Run.Id, Owner, extendedLease, default));

        extended.ShouldBeTrue();
        var row = await harness.StoreAsync(s => s.GetRunAsync(enqueued.Run.Id, default));
        row!.LeaseExpiresAt.ShouldBe(extendedLease);
    }
}
