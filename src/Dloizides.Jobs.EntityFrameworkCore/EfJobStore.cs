using System.Linq.Expressions;
using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Dloizides.Jobs.EntityFrameworkCore;

/// <summary>
/// The EF Core <see cref="IJobStore"/>. Every mutation is a COMPARE-AND-SET expressed as a single
/// conditional <c>ExecuteUpdateAsync</c> — an atomic storage-side UPDATE that carries NO change-tracker
/// state, so it cannot lose to a concurrent writer via a read-modify-write and it cannot throw a
/// <see cref="DbUpdateConcurrencyException"/>. "0 rows" means another party already moved the row; the
/// caller reads that as a benign no-op. This is the exact shape that ended AML's reclaim-vs-complete loop,
/// generalised.
/// </summary>
/// <typeparam name="TContext">The consuming app's DbContext, with <see cref="JobRun"/> in its model.</typeparam>
public sealed class EfJobStore<TContext> : IJobStore
    where TContext : DbContext
{
    private readonly TContext _db;
    private readonly TimeProvider _time;

    /// <summary>Construct the store over the app's DbContext.</summary>
    public EfJobStore(TContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private DbSet<JobRun> Runs => _db.Set<JobRun>();

    /// <inheritdoc />
    public async Task<JobEnqueueResult> EnqueueAsync(JobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var now = _time.GetUtcNow();

        var occupying = await FindSlotOccupantAsync(run, cancellationToken).ConfigureAwait(false);
        if (occupying is not null)
        {
            if (IsLeaseLive(occupying, now))
            {
                return JobEnqueueResult.AlreadyRunning(occupying);
            }

            await ReclaimStrandedAsync(occupying, now, cancellationToken).ConfigureAwait(false);
        }

        Runs.Add(run);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the single-flight race: another replica's insert committed first. The index — not the
            // pre-read — is what made that safe. Detach our rejected row and report the winner.
            _db.Entry(run).State = EntityState.Detached;
            var winner = await FindSlotOccupantAsync(run, cancellationToken).ConfigureAwait(false);
            return winner is not null ? JobEnqueueResult.AlreadyRunning(winner) : JobEnqueueResult.Queued(run);
        }

        return JobEnqueueResult.Queued(run);
    }

    /// <inheritdoc />
    /// <remarks>For a per-argument job this is the newest occupier across ALL its arguments; the enqueue path
    /// checks the run's own (job, key) slot instead.</remarks>
    public Task<JobRun?> FindOccupyingRunAsync(string jobName, CancellationToken cancellationToken) =>
        Runs.AsNoTracking()
            .Where(r => r.JobName == jobName && (r.Outcome == JobRunOutcomes.Queued || r.Outcome == JobRunOutcomes.Running))
            .OrderByDescending(r => r.TriggeredAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<JobRun?> ClaimNextAsync(
        string owner, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        RequireRelational();

        var candidate = await Runs.AsNoTracking()
            .Where(r => r.Outcome == JobRunOutcomes.Queued
                || (r.Outcome == JobRunOutcomes.Running && r.LeaseExpiresAt != null && r.LeaseExpiresAt < now))
            .OrderBy(r => r.TriggeredAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        // Pin the CAS to the EXACT state observed: a still-queued row, OR the same running row with the same
        // lapsed lease and owner. Two racing claimers both see the candidate; only the first flips it, and
        // the loser matches 0 rows because the outcome/lease/owner it required has moved.
        var observedLease = candidate.LeaseExpiresAt;
        var observedOwner = candidate.ClaimedBy;
        var affected = await Runs
            .Where(r => r.Id == candidate.Id
                && (r.Outcome == JobRunOutcomes.Queued
                    || (r.Outcome == JobRunOutcomes.Running
                        && r.LeaseExpiresAt == observedLease
                        && r.ClaimedBy == observedOwner)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, JobRunOutcomes.Running)
                .SetProperty(r => r.ClaimedBy, owner)
                .SetProperty(r => r.LeaseExpiresAt, leaseExpiresAt)
                .SetProperty(r => r.StartedAt, r => r.StartedAt ?? now), cancellationToken)
            .ConfigureAwait(false);
        if (affected == 0)
        {
            return null;
        }

        // Reload the row we now own — carrying its Checkpoint so the runner can RESUME from it.
        return await Runs.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == candidate.Id && r.ClaimedBy == owner, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> HeartbeatAsync(
        Guid runId, string owner, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken) =>
        RunOwnedUpdateAsync(runId, owner, s => s.SetProperty(r => r.LeaseExpiresAt, leaseExpiresAt), cancellationToken);

    /// <inheritdoc />
    public Task<bool> SaveCheckpointAsync(
        Guid runId, string owner, string checkpoint, CancellationToken cancellationToken) =>
        RunOwnedUpdateAsync(runId, owner, s => s.SetProperty(r => r.Checkpoint, checkpoint), cancellationToken);

    /// <inheritdoc />
    public Task<bool> ReportProgressAsync(
        Guid runId, string owner, string progress, CancellationToken cancellationToken) =>
        RunOwnedUpdateAsync(runId, owner, s => s.SetProperty(r => r.Progress, progress), cancellationToken);

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(
        Guid runId, string owner, string outcome, string? error, DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        RequireRelational();
        var affected = await Runs
            .Where(r => r.Id == runId && r.Outcome == JobRunOutcomes.Running && r.ClaimedBy == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, outcome)
                .SetProperty(r => r.CompletedAt, completedAt)
                .SetProperty(r => r.Error, error)
                .SetProperty(r => r.ClaimedBy, (string?)null)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public Task<JobRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken) =>
        Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastSuccessAtAsync(string jobName, CancellationToken cancellationToken)
    {
        var run = await Runs.AsNoTracking()
            .Where(r => r.JobName == jobName && r.Outcome == JobRunOutcomes.Completed && r.CompletedAt != null)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return run?.CompletedAt;
    }

    /// <inheritdoc />
    public Task<JobRun?> GetLastFinishedRunAsync(string jobName, CancellationToken cancellationToken) =>
        Runs.AsNoTracking()
            .Where(r => r.JobName == jobName && r.CompletedAt != null)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRun>> GetRecentRunsAsync(
        string jobName, int limit, CancellationToken cancellationToken)
    {
        var runs = await Runs.AsNoTracking()
            .Where(r => r.JobName == jobName)
            .OrderByDescending(r => r.TriggeredAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return runs;
    }

    /// <summary>
    /// The occupier of THIS run's slot. Default mapping: any unfinished run of the job (the pre-1.2.0 query,
    /// unchanged). Per-argument mapping: an unfinished run with the same (JobName, SingleFlightKey). A non-empty
    /// key on a model without the per-argument mapping is refused rather than silently collapsed to global.
    /// </summary>
    private Task<JobRun?> FindSlotOccupantAsync(JobRun run, CancellationToken cancellationToken)
    {
        var keyMapped = _db.Model.FindEntityType(typeof(JobRun))?.FindProperty(nameof(JobRun.SingleFlightKey)) is not null;
        if (!keyMapped)
        {
            if (!string.IsNullOrEmpty(run.SingleFlightKey))
            {
                throw new InvalidOperationException(
                    $"Job '{run.JobName}' is SingleFlightScope.PerArgument, but the DbContext maps JobRun without "
                    + "the per-argument single-flight index. Call ApplyJobRunConfiguration(isNpgsql, "
                    + "perArgumentSingleFlight: true) in OnModelCreating and add a migration.");
            }

            return FindOccupyingRunAsync(run.JobName, cancellationToken);
        }

        var jobName = run.JobName;
        var key = run.SingleFlightKey;
        return Runs.AsNoTracking()
            .Where(r => r.JobName == jobName && r.SingleFlightKey == key
                && (r.Outcome == JobRunOutcomes.Queued || r.Outcome == JobRunOutcomes.Running))
            .OrderByDescending(r => r.TriggeredAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> RunOwnedUpdateAsync(
        Guid runId,
        string owner,
        Expression<Func<SetPropertyCalls<JobRun>, SetPropertyCalls<JobRun>>> set,
        CancellationToken cancellationToken)
    {
        RequireRelational();
        var affected = await Runs
            .Where(r => r.Id == runId && r.Outcome == JobRunOutcomes.Running && r.ClaimedBy == owner)
            .ExecuteUpdateAsync(set, cancellationToken)
            .ConfigureAwait(false);
        return affected > 0;
    }

    private async Task ReclaimStrandedAsync(JobRun stranded, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireRelational();
        var error = $"Lease expired at {stranded.LeaseExpiresAt:O}; owner {stranded.ClaimedBy} presumed dead.";
        var observedLease = stranded.LeaseExpiresAt;

        // CAS on the exact lapsed state so this can never collide with the run's real owner (or another
        // reclaimer) racing the same row. 0 rows = someone else already resolved it; the slot is free either
        // way and the insert that follows either takes it or loses the single-flight race.
        await Runs
            .Where(r => r.Id == stranded.Id
                && (r.Outcome == JobRunOutcomes.Queued || r.Outcome == JobRunOutcomes.Running)
                && r.LeaseExpiresAt == observedLease)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, JobRunOutcomes.Failed)
                .SetProperty(r => r.CompletedAt, now)
                .SetProperty(r => r.Error, error), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsLeaseLive(JobRun run, DateTimeOffset now) =>
        run.LeaseExpiresAt is null || run.LeaseExpiresAt > now;

    private void RequireRelational()
    {
        if (!_db.Database.IsRelational())
        {
            throw new NotSupportedException(
                "Dloizides.Jobs.EntityFrameworkCore requires a RELATIONAL provider: its compare-and-set "
                + "transitions run as ExecuteUpdate, and the single-flight guarantee needs a real unique "
                + "index. The in-memory provider ignores both, so it cannot exercise the concurrency "
                + "guarantees this package exists to provide. Use SQLite, Postgres, or another relational "
                + "provider (in tests too).");
        }
    }

    /// <summary>
    /// Whether an update failure is the single-flight index rejecting a duplicate. Matched provider-agnostic
    /// on the SQLSTATE (Npgsql <c>23505</c>) or the SQLite constraint code, reflected by name so no provider
    /// package is referenced here.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        const string PostgresUniqueViolation = "23505";
        const int SqliteConstraint = 19;

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            if (type.GetProperty("SqlState")?.GetValue(inner) as string == PostgresUniqueViolation)
            {
                return true;
            }

            if (type.GetProperty("SqliteErrorCode")?.GetValue(inner) is int code && code == SqliteConstraint)
            {
                return true;
            }
        }

        return false;
    }
}
