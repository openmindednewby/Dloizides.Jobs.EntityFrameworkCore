using Dloizides.Jobs.EntityFrameworkCore;
using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>
/// A minimal consumer DbContext: it maps <see cref="JobRun"/> through the package's own configuration
/// helper, exactly as an adopting service would. isNpgsql:false because the tests run on SQLite — the
/// relational provider that genuinely executes the compare-and-set and enforces the single-flight index.
/// </summary>
public sealed class TestJobsDbContext : DbContext
{
    public TestJobsDbContext(DbContextOptions<TestJobsDbContext> options)
        : base(options)
    {
    }

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyJobRunConfiguration(isNpgsql: false);
    }
}
