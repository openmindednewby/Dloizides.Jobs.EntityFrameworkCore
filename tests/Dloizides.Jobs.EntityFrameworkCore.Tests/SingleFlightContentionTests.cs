using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// Single-flight: at most one queued-or-running run per job name across every replica. Proven three ways —
/// the sequential common case, the raw index that is the actual guarantee, and true concurrent contention.
/// </summary>
public sealed class SingleFlightContentionTests
{
    [Fact]
    public async Task SecondEnqueue_WhileOccupied_ReportsAlreadyRunning()
    {
        using var harness = TestHarness.Create();

        var first = await harness.StoreAsync(s => s.EnqueueAsync(NewQueuedRun(harness), default));
        var second = await harness.StoreAsync(s => s.EnqueueAsync(NewQueuedRun(harness), default));

        first.Accepted.ShouldBeTrue();
        second.Accepted.ShouldBeFalse();
        second.Run.Id.ShouldBe(first.Run.Id); // reports the occupier, not a duplicate
    }

    [Fact]
    public async Task DirectDuplicateInsert_IsRejectedByTheSingleFlightIndex()
    {
        using var harness = TestHarness.Create();

        using var scope = harness.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestJobsDbContext>();
        db.JobRuns.Add(NewQueuedRun(harness));
        db.JobRuns.Add(NewQueuedRun(harness)); // second queued run of the SAME job

        // The database itself — not any app-level check — refuses the duplicate. This is the guarantee the
        // in-memory provider could never exercise.
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrentEnqueues_ProduceExactlyOneRun()
    {
        using var harness = TestHarness.Create();
        const int Racers = 8;

        var results = await Task.WhenAll(Enumerable.Range(0, Racers)
            .Select(_ => harness.StoreAsync(s => s.EnqueueAsync(NewQueuedRun(harness), default))));

        results.Count(r => r.Accepted).ShouldBe(1); // exactly one winner
        results.Count(r => !r.Accepted).ShouldBe(Racers - 1); // the rest see already-running

        var rows = await harness.StoreAsync(s => s.GetRecentRunsAsync(SimpleJob.JobName, 100, default));
        rows.Count.ShouldBe(1); // and only one row was ever created
    }

    [Fact]
    public async Task ConcurrentClaims_ExactlyOneWins()
    {
        using var harness = TestHarness.Create();
        await harness.StoreAsync(s => s.EnqueueAsync(NewQueuedRun(harness), default));
        var now = harness.Time.GetUtcNow();

        var claims = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => harness.StoreAsync(s =>
                s.ClaimNextAsync($"owner-{i}", now, now + TimeSpan.FromSeconds(120), default))));

        claims.Count(r => r is not null).ShouldBe(1); // exactly one claimant runs it; the losers no-op
    }

    private static JobRun NewQueuedRun(TestHarness harness) => new()
    {
        JobName = SimpleJob.JobName,
        TriggerSource = JobTriggerSources.Manual,
        TriggeredBy = "tester",
        TriggeredAt = harness.Time.GetUtcNow(),
        Outcome = JobRunOutcomes.Queued,
    };
}
