using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;

namespace Dloizides.Jobs.EntityFrameworkCore;

/// <summary>
/// The one line a consumer's <c>DbContext.OnModelCreating</c> calls to map <see cref="JobRun"/> (jsonb
/// columns + the single-flight index). Add a <c>JobRun</c> migration afterwards and the runner's storage is
/// in place.
/// </summary>
public static class JobRunModelBuilderExtensions
{
    /// <summary>
    /// Apply the <see cref="JobRun"/> mapping, detecting Postgres from the model's own annotations so
    /// Checkpoint/Progress become <c>jsonb</c> there and plain string columns elsewhere. When the provider
    /// is configured after <c>OnModelCreating</c> and not yet visible on the model, use the explicit
    /// <see cref="ApplyJobRunConfiguration(ModelBuilder, bool)"/> overload instead.
    /// </summary>
    public static ModelBuilder ApplyJobRunConfiguration(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var isNpgsql = modelBuilder.Model.GetAnnotations()
            .Any(a => a.Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        return modelBuilder.ApplyJobRunConfiguration(isNpgsql);
    }

    /// <summary>
    /// Apply the mapping, stating explicitly whether the provider is Npgsql (jsonb columns). Use this when
    /// the caller already tracks its provider (the pattern the AML context uses: <c>Database.IsNpgsql()</c>).
    /// </summary>
    public static ModelBuilder ApplyJobRunConfiguration(this ModelBuilder modelBuilder, bool isNpgsql)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new JobRunEntityConfiguration(isNpgsql));
        return modelBuilder;
    }

    /// <summary>
    /// Apply the mapping with an explicit single-flight shape. Pass <paramref name="perArgumentSingleFlight"/>
    /// = true to map <see cref="JobRun.SingleFlightKey"/> and key the single-flight index on
    /// (<c>JobName</c>, <c>SingleFlightKey</c>), required before any job declares
    /// <see cref="SingleFlightScope.PerArgument"/>. Changes the schema: add a migration.
    /// </summary>
    public static ModelBuilder ApplyJobRunConfiguration(
        this ModelBuilder modelBuilder, bool isNpgsql, bool perArgumentSingleFlight)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new JobRunEntityConfiguration(isNpgsql, perArgumentSingleFlight));
        return modelBuilder;
    }
}
