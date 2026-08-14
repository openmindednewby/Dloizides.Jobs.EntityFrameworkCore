using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// The rule that ended the reclaim-vs-complete error loop: an owner completing a run that has already been
/// reclaimed must be a BENIGN no-op — no throw, no clobber of the reclaimer's outcome. The second test
/// proves WHY the compare-and-set is necessary: a naive tracked <c>SaveChanges</c> clobbers instead.
/// </summary>
public sealed class ConflictSafeNoOpTests
{
    private const string OwnerA = "pod-a";
    private const string OwnerB = "pod-b";

    [Fact]
    public async Task Complete_OnRunAlreadyReclaimed_IsBenignNoOp_AndDoesNotClobber()
    {
        using var harness = TestHarness.Create();
        var runId = await EnqueueSimpleAsync(harness);

        // A claims it.
        var now = harness.Time.GetUtcNow();
        var claimedByA = await harness.StoreAsync(s => s.ClaimNextAsync(OwnerA, now, now + TimeSpan.FromSeconds(120), default));
        claimedByA!.Id.ShouldBe(runId);

        // A's lease lapses; B reclaims.
        harness.Time.Advance(TimeSpan.FromSeconds(121));
        var now2 = harness.Time.GetUtcNow();
        var claimedByB = await harness.StoreAsync(s => s.ClaimNextAsync(OwnerB, now2, now2 + TimeSpan.FromSeconds(120), default));
        claimedByB!.Id.ShouldBe(runId);
        claimedByB.ClaimedBy.ShouldBe(OwnerB);

        // A — not knowing it was reclaimed — tries to record completion. Compare-and-set: 0 rows.
        var recorded = await harness.StoreAsync(s =>
            s.CompleteAsync(runId, OwnerA, JobRunOutcomes.Completed, null, harness.Time.GetUtcNow(), default));

        recorded.ShouldBeFalse(); // benign no-op, never a throw
        var row = await harness.StoreAsync(s => s.GetRunAsync(runId, default));
        row!.Outcome.ShouldBe(JobRunOutcomes.Running); // NOT clobbered to completed
        row.ClaimedBy.ShouldBe(OwnerB); // the reclaimer still owns it — the slot is B's
    }

    [Fact]
    public async Task NaiveTrackedComplete_OnReclaimedRun_Clobbers_ProvingCasIsNeeded()
    {
        using var harness = TestHarness.Create();
        var runId = await EnqueueSimpleAsync(harness);

        var now = harness.Time.GetUtcNow();
        await harness.StoreAsync(s => s.ClaimNextAsync(OwnerA, now, now + TimeSpan.FromSeconds(120), default));

        // A holds a STALE tracked handle to the row (as it would after its own claim).
        using var scope = harness.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestJobsDbContext>();
        var tracked = await db.JobRuns.FirstAsync(r => r.Id == runId);
        tracked.ClaimedBy.ShouldBe(OwnerA);

        // Meanwhile B reclaims (through the CAS store, on its own connection).
        harness.Time.Advance(TimeSpan.FromSeconds(121));
        var now2 = harness.Time.GetUtcNow();
        await harness.StoreAsync(s => s.ClaimNextAsync(OwnerB, now2, now2 + TimeSpan.FromSeconds(120), default));

        // The NAIVE finalisation: mutate the stale tracked entity and SaveChanges. With no concurrency
        // token this emits UPDATE ... WHERE Id=@id and OVERWRITES B's reclaim — the very bug the store's
        // compare-and-set exists to prevent.
        tracked.Outcome = JobRunOutcomes.Completed;
        tracked.CompletedAt = now2;
        tracked.ClaimedBy = null;
        await db.SaveChangesAsync();

        var row = await harness.StoreAsync(s => s.GetRunAsync(runId, default));
        row!.Outcome.ShouldBe(JobRunOutcomes.Completed); // clobbered
        row.ClaimedBy.ShouldBeNull(); // B's ownership was lost — exactly what the CAS store refuses to do
    }

    private static async Task<Guid> EnqueueSimpleAsync(TestHarness harness)
    {
        var run = new JobRun
        {
            JobName = SimpleJob.JobName,
            TriggerSource = JobTriggerSources.Manual,
            TriggeredBy = "tester",
            TriggeredAt = harness.Time.GetUtcNow(),
            Outcome = JobRunOutcomes.Queued,
        };
        var result = await harness.StoreAsync(s => s.EnqueueAsync(run, default));
        result.Accepted.ShouldBeTrue();
        return result.Run.Id;
    }
}
