using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.EntityFrameworkCore.Tests.Support;
using Dloizides.Jobs.Extensions;
using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests;

/// <summary>
/// SingleFlightScope.PerArgument: one unfinished run per (job, argument), opt-in. Also pins that the DEFAULT
/// mapping is untouched (no key column, index on JobName alone) and that a global job keeps its single slot.
/// </summary>
public sealed class PerArgumentSingleFlightTests
{
    [Fact]
    public void DefaultMapping_DoesNotMapTheKey_AndIndexesJobNameOnly()
    {
        using var harness = TestHarness.Create();
        using var scope = harness.Provider.CreateScope();
        var entity = scope.ServiceProvider.GetRequiredService<TestJobsDbContext>().Model.FindEntityType(typeof(JobRun))!;

        entity.FindProperty(nameof(JobRun.SingleFlightKey)).ShouldBeNull();
        var index = entity.GetIndexes().Single(i => i.GetDatabaseName() == JobRunEntityConfiguration.SingleFlightIndexName);
        index.Properties.Select(p => p.Name).ShouldBe(new[] { nameof(JobRun.JobName) });
    }

    [Fact]
    public async Task GlobalJob_DifferentArguments_StillShareOneSlot()
    {
        using var harness = TestHarness.Create();

        var first = await TriggerAsync(harness.Provider, SimpleJob.JobName, "user-a");
        var second = await TriggerAsync(harness.Provider, SimpleJob.JobName, "user-b");

        first.Status.ShouldBe(JobTriggerStatus.Accepted);
        second.Status.ShouldBe(JobTriggerStatus.AlreadyRunning);
    }

    [Fact]
    public async Task PerArgumentJob_OnDefaultMapping_IsRefusedNotSilentlyGlobal()
    {
        using var harness = TestHarness.Create(extraJobs: j => j.AddJob<PerUserJob>());

        await Should.ThrowAsync<InvalidOperationException>(() => TriggerAsync(harness.Provider, PerUserJob.JobName, "user-a"));
    }

    [Fact]
    public async Task PerArgumentJob_DifferentArguments_BothAccepted()
    {
        using var host = PerArgumentHost.Create();

        var a = await TriggerAsync(host.Provider, PerUserJob.JobName, "user-a");
        var b = await TriggerAsync(host.Provider, PerUserJob.JobName, "user-b");

        a.Status.ShouldBe(JobTriggerStatus.Accepted);
        b.Status.ShouldBe(JobTriggerStatus.Accepted);
        (await host.CountUnfinishedAsync(PerUserJob.JobName)).ShouldBe(2); // both rows really persisted
    }

    [Fact]
    public async Task PerArgumentJob_SameArgument_ReportsAlreadyRunning()
    {
        using var host = PerArgumentHost.Create();

        var first = await TriggerAsync(host.Provider, PerUserJob.JobName, "user-a");
        var second = await TriggerAsync(host.Provider, PerUserJob.JobName, "user-a");

        second.Status.ShouldBe(JobTriggerStatus.AlreadyRunning);
        second.Run!.Id.ShouldBe(first.Run!.Id);
    }

    [Fact]
    public async Task PerArgumentJob_NullAndEmptyArguments_ShareOneSlot()
    {
        using var host = PerArgumentHost.Create();

        var first = await TriggerAsync(host.Provider, PerUserJob.JobName, null);
        var second = await TriggerAsync(host.Provider, PerUserJob.JobName, null);
        var third = await TriggerAsync(host.Provider, PerUserJob.JobName, string.Empty);

        first.Status.ShouldBe(JobTriggerStatus.Accepted);
        second.Status.ShouldBe(JobTriggerStatus.AlreadyRunning);
        third.Status.ShouldBe(JobTriggerStatus.AlreadyRunning);
    }

    [Fact]
    public async Task GlobalJob_OnPerArgumentMapping_KeepsOneSlot()
    {
        using var host = PerArgumentHost.Create();

        var first = await TriggerAsync(host.Provider, SimpleJob.JobName, "x");
        var second = await TriggerAsync(host.Provider, SimpleJob.JobName, "y");

        first.Status.ShouldBe(JobTriggerStatus.Accepted);
        second.Status.ShouldBe(JobTriggerStatus.AlreadyRunning);
    }

    [Fact]
    public async Task DirectDuplicateInsert_SameKey_IsRejectedByTheIndex()
    {
        using var host = PerArgumentHost.Create();
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PerArgumentJobsDbContext>();
        db.JobRuns.Add(NewRun("user-a"));
        db.JobRuns.Add(NewRun("user-a"));

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrentEnqueues_TwoArguments_ExactlyOneWinnerEach()
    {
        using var host = PerArgumentHost.Create();
        const int RacersPerArgument = 6;

        var results = await Task.WhenAll(Enumerable.Range(0, RacersPerArgument * 2)
            .Select(i => TriggerAsync(host.Provider, PerUserJob.JobName, i % 2 == 0 ? "user-a" : "user-b")));

        results.Count(r => r.Status == JobTriggerStatus.Accepted).ShouldBe(2);
        (await host.CountUnfinishedAsync(PerUserJob.JobName)).ShouldBe(2);
    }

    private static JobRun NewRun(string key) => new()
    {
        JobName = PerUserJob.JobName,
        TriggerSource = JobTriggerSources.Manual,
        TriggeredBy = "tester",
        TriggeredAt = DateTimeOffset.Parse("2026-08-14T09:00:00Z"),
        Argument = key,
        SingleFlightKey = key,
        Outcome = JobRunOutcomes.Queued,
    };

    private static async Task<JobTriggerResult> TriggerAsync(IServiceProvider provider, string jobName, string? argument)
    {
        using var scope = provider.CreateScope();
        var trigger = scope.ServiceProvider.GetRequiredService<IJobTrigger>();
        return await trigger.TriggerAsync(jobName, JobTriggerSources.Manual, "tester", null, argument, default);
    }
}

/// <summary>A job that opts in to per-argument single-flight (one run per user id).</summary>
public sealed class PerUserJob : ICheckpointableJob
{
    public const string JobName = "per-user";

    public string Name => JobName;

    public JobCadence Cadence => JobCadence.None;

    public SingleFlightScope SingleFlightScope => SingleFlightScope.PerArgument;

    public Task RunAsync(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A consumer context that opts in to the per-argument mapping. A separate TYPE because EF caches the
/// model per context type.</summary>
public sealed class PerArgumentJobsDbContext : DbContext
{
    public PerArgumentJobsDbContext(DbContextOptions<PerArgumentJobsDbContext> options)
        : base(options)
    {
    }

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyJobRunConfiguration(isNpgsql: false, perArgumentSingleFlight: true);
    }
}

/// <summary>File-backed SQLite host over <see cref="PerArgumentJobsDbContext"/>, so the real index fires.</summary>
internal sealed class PerArgumentHost : IDisposable
{
    private readonly string _dbPath;

    private PerArgumentHost(ServiceProvider provider, string dbPath)
    {
        Provider = provider;
        _dbPath = dbPath;
    }

    public ServiceProvider Provider { get; }

    public static PerArgumentHost Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dloizides-jobs-perarg-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new TestTimeProvider(DateTimeOffset.Parse("2026-08-14T09:00:00Z")));
        services.AddDbContext<PerArgumentJobsDbContext>(o => o.UseSqlite($"DataSource={dbPath}"));
        services.AddDloizidesJobs(jobs =>
        {
            jobs.AddJob<SimpleJob>();
            jobs.AddJob<PerUserJob>();
            jobs.UseEntityFrameworkStore<PerArgumentJobsDbContext>();
        });

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<PerArgumentJobsDbContext>().Database.EnsureCreated();
        }

        return new PerArgumentHost(provider, dbPath);
    }

    public async Task<int> CountUnfinishedAsync(string jobName)
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PerArgumentJobsDbContext>();
        return await db.JobRuns.CountAsync(r => r.JobName == jobName
            && (r.Outcome == JobRunOutcomes.Queued || r.Outcome == JobRunOutcomes.Running));
    }

    public void Dispose()
    {
        Provider.Dispose();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // A pooled SQLite handle may still hold the file on Windows; a leftover temp file is harmless.
        }
    }
}
