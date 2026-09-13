using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dloizides.Jobs.EntityFrameworkCore;

/// <summary>
/// Maps <see cref="JobRun"/>: the jsonb Checkpoint/Progress columns and — the crux — the single-flight
/// PARTIAL UNIQUE INDEX over the unfinished outcomes. That index is what makes "one run per job across all
/// replicas" a database guarantee two racing pods contend for, rather than agreement between them.
/// </summary>
public sealed class JobRunEntityConfiguration : IEntityTypeConfiguration<JobRun>
{
    /// <summary>The single-flight index name — stable so migrations and diagnostics can refer to it.</summary>
    public const string SingleFlightIndexName = "IX_JobRuns_SingleFlight";

    /// <summary>Max length of <see cref="JobRun.JobName"/> (the single-flight key).</summary>
    public const int JobNameMaxLength = 200;

    /// <summary>Max length of the short enum-like columns (outcome, trigger source).</summary>
    public const int EnumMaxLength = 32;

    /// <summary>Max length of <see cref="JobRun.TriggeredBy"/> (a subject claim / actor id).</summary>
    public const int ActorMaxLength = 256;

    /// <summary>Max length of <see cref="JobRun.ClaimedBy"/> (host + run-unique token).</summary>
    public const int OwnerMaxLength = 256;

    /// <summary>Max length of <see cref="JobRun.Argument"/> (an opaque trigger input).</summary>
    public const int ArgumentMaxLength = 1024;

    private readonly bool _isNpgsql;
    private readonly bool _perArgumentSingleFlight;

    /// <summary>Construct the configuration.</summary>
    /// <param name="isNpgsql">True to emit Postgres <c>jsonb</c> column types for Checkpoint/Progress;
    /// false to leave them as the provider's default string type (e.g. TEXT on SQLite).</param>
    public JobRunEntityConfiguration(bool isNpgsql)
        : this(isNpgsql, perArgumentSingleFlight: false)
    {
    }

    /// <summary>Construct the configuration, choosing the single-flight index shape.</summary>
    /// <param name="isNpgsql">See <see cref="JobRunEntityConfiguration(bool)"/>.</param>
    /// <param name="perArgumentSingleFlight">False (the default shape): the index is on <c>JobName</c> alone
    /// and <see cref="JobRun.SingleFlightKey"/> is not mapped, so the schema is exactly the pre-1.2.0 one.
    /// True: a non-null <c>SingleFlightKey</c> column is mapped and the index becomes
    /// (<c>JobName</c>, <c>SingleFlightKey</c>), which lets <see cref="SingleFlightScope.PerArgument"/> jobs run
    /// one-per-argument while global jobs (key always empty) keep one slot. Needs a migration.</param>
    public JobRunEntityConfiguration(bool isNpgsql, bool perArgumentSingleFlight)
    {
        _isNpgsql = isNpgsql;
        _perArgumentSingleFlight = perArgumentSingleFlight;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        builder.ToTable("JobRuns");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.JobName).IsRequired().HasMaxLength(JobNameMaxLength);
        builder.Property(r => r.TriggerSource).IsRequired().HasMaxLength(EnumMaxLength);
        builder.Property(r => r.TriggeredBy).IsRequired().HasMaxLength(ActorMaxLength);
        builder.Property(r => r.TriggeredAt).IsRequired();
        builder.Property(r => r.Outcome).IsRequired().HasMaxLength(EnumMaxLength);
        builder.Property(r => r.ClaimedBy).HasMaxLength(OwnerMaxLength);
        builder.Property(r => r.Argument).HasMaxLength(ArgumentMaxLength);

        if (_isNpgsql)
        {
            // Opaque resume state and progress snapshots live as jsonb so a consumer can query into them.
            builder.Property(r => r.Checkpoint).HasColumnType("jsonb");
            builder.Property(r => r.Progress).HasColumnType("jsonb");
        }
        else
        {
            // SQLite (and any provider without native timestamptz ordering) cannot ORDER BY / compare a
            // DateTimeOffset — it stores it as TEXT with no ordering guarantee. Persist the timestamps as
            // sortable UTC ticks there so the claim/history/lease queries translate. All job timestamps are
            // UTC (minted from TimeProvider.GetUtcNow), so no offset is lost. Postgres keeps native columns.
            var dto = new ValueConverter<DateTimeOffset, long>(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            var nullableDto = new ValueConverter<DateTimeOffset?, long?>(
                v => v.HasValue ? v.Value.UtcTicks : null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

            builder.Property(r => r.TriggeredAt).HasConversion(dto);
            builder.Property(r => r.StartedAt).HasConversion(nullableDto);
            builder.Property(r => r.CompletedAt).HasConversion(nullableDto);
            builder.Property(r => r.LeaseExpiresAt).HasConversion(nullableDto);
        }

        // Read per job, newest first — the history/timeline access path.
        builder.HasIndex(r => new { r.JobName, r.TriggeredAt });

        // THE SINGLE-FLIGHT GUARANTEE. At most one queued-or-running run per job name, enforced by the
        // database. Two replicas triggering at once produce one insert and one unique violation, which the
        // store converts into an explicit already-running result. The filter SQL uses double-quoted
        // identifiers, valid on both Npgsql and SQLite (the relational providers this package targets).
        var filter = $"\"Outcome\" IN ('{JobRunOutcomes.Queued}', '{JobRunOutcomes.Running}')";
        if (_perArgumentSingleFlight)
        {
            // Per-argument: the key column is NOT NULL with an empty default, so Postgres' NULLS-DISTINCT unique
            // semantics can never let a missing argument bypass the slot.
            builder.Property(r => r.SingleFlightKey)
                .IsRequired()
                .HasMaxLength(ArgumentMaxLength)
                .HasDefaultValue(string.Empty);
            builder.HasIndex(r => new { r.JobName, r.SingleFlightKey })
                .IsUnique()
                .HasDatabaseName(SingleFlightIndexName)
                .HasFilter(filter);
            return;
        }

        // Default: the key is not mapped at all, so the schema and index are the pre-1.2.0 ones exactly.
        builder.Ignore(r => r.SingleFlightKey);
        builder.HasIndex(r => r.JobName)
            .IsUnique()
            .HasDatabaseName(SingleFlightIndexName)
            .HasFilter(filter);
    }
}
