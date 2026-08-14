# Dloizides.Jobs.EntityFrameworkCore

The **Entity Framework Core store** for [`Dloizides.Jobs`](https://github.com/openmindednewby/Dloizides.Jobs) —
the persistence half of the checkpointed background-jobs standard. It maps `JobRun` (jsonb checkpoint /
progress on Postgres), creates the **single-flight partial unique index**, and implements every state
transition as a **compare-and-set `ExecuteUpdate`** so a reclaim-vs-complete race is a benign no-op instead
of a poll-killing `DbUpdateConcurrencyException`.

## Why compare-and-set, not tracked `SaveChanges`

The runner's heartbeat advances a run's row while the job works. A tracked completion carries the row
version it read AT CLAIM TIME, matches 0 rows against the heartbeated row, and throws
`DbUpdateConcurrencyException` — which aborted the whole poll and re-ran the job forever. The store's
conditional `UPDATE ... WHERE Id = @id AND Outcome = 'running' AND ClaimedBy = @owner` carries no tracker
state: it finalises the row **only while it is still running under this owner**, so the owner's own
heartbeats never collide with its own completion, and a reclaimed row simply matches 0 rows — a benign
no-op the runner drops cleanly.

## Wiring

1. Map `JobRun` in your `DbContext`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyJobRunConfiguration();            // auto-detects Npgsql -> jsonb columns
    // or: modelBuilder.ApplyJobRunConfiguration(Database.IsNpgsql());
}
```

2. Add a migration for the new `JobRuns` table + the single-flight index.

3. Select the store inside `AddDloizidesJobs`:

```csharp
builder.AddDloizidesJobs(jobs =>
{
    jobs.AddJob<IngestJob>();
    jobs.UseEntityFrameworkStore<AppDbContext>();
});
```

## What it maps

- `Checkpoint` / `Progress` → **jsonb** on Postgres (plain string/TEXT elsewhere); on SQLite the timestamps
  convert to sortable UTC ticks so the claim/history queries translate.
- **Single-flight index** `IX_JobRuns_SingleFlight` — `UNIQUE (JobName) WHERE Outcome IN ('queued','running')`.
- CAS transitions: `ClaimNextAsync` (claim queued **or** reclaim a lapsed lease, preserving `StartedAt` and
  the checkpoint for resume), `HeartbeatAsync`, `SaveCheckpointAsync`, `ReportProgressAsync`,
  `CompleteAsync` — each conditioned on the observed state, returning `false`/`null` on 0 rows.

## Postgres status backplane (real-time PUSH)

This package also ships the **Postgres `LISTEN`/`NOTIFY` status backplane** — cross-pod real-time push with
**no new infra**, reusing the same database as the store. Select it inside `AddDloizidesJobs`:

```csharp
jobs.UseEntityFrameworkStore<AppDbContext>();
jobs.UsePostgresStatusBackplane<AppDbContext>();   // reads the connection string from AppDbContext
```

```jsonc
"Jobs": { "Status": { "Backplane": "Postgres", "Channel": "dloizides_jobs" } }
```

The publishing pod issues `pg_notify(channel, payload)`; every pod holds one dedicated `LISTEN` connection
and re-emits each event to local subscribers (the [`Dloizides.Jobs.AspNetCore`](https://github.com/openmindednewby/Dloizides.Jobs.AspNetCore)
wire then re-reads the durable `JobStatus` and streams it). **Persist first, push second** — `NOTIFY` is not
durable, so a notification lost to a reconnect is simply healed by the next poll. The `LISTEN` loop is a
hosted service that stays dormant unless `Jobs:Status:Backplane=Postgres`, and reconnects with backoff.

> This adds a transitive **Npgsql** (raw ADO driver) reference — `LISTEN`/`NOTIFY` has no provider-agnostic
> EF surface. It is inert unless the Postgres backplane is selected.

## Requires a relational provider

The store throws `NotSupportedException` on the in-memory provider by design: it ignores both `ExecuteUpdate`
and the unique index, so it cannot exercise the concurrency guarantees this package exists to provide. Use
Postgres in production and SQLite (or a Postgres Testcontainer) in tests — the bundled test suite runs on
SQLite so the compare-and-set genuinely fires.

## License

MIT
