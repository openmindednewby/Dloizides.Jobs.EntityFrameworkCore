using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Extensions;
using Dloizides.Jobs.Hosting;
using Dloizides.Jobs.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Wires the full stack — core runtime + EF store — over a FILE-BACKED SQLite database so the compare-and-set
/// and single-flight index genuinely fire, and multiple connections (one per scope) can contend. A fresh
/// temp database per harness keeps tests isolated; SQLite's default command-timeout retries serialise the
/// concurrent-contention test rather than throwing "database is locked".
/// </summary>
public sealed class TestHarness : IDisposable
{
    private readonly string _dbPath;

    private TestHarness(ServiceProvider provider, TestTimeProvider time, JobProbe probe, string dbPath)
    {
        Provider = provider;
        Time = time;
        Probe = probe;
        _dbPath = dbPath;
    }

    public ServiceProvider Provider { get; }

    public TestTimeProvider Time { get; }

    public JobProbe Probe { get; }

    public static TestHarness Create(
        DateTimeOffset? start = null,
        Action<IServiceCollection>? preConfigure = null,
        Action<JobsBuilder>? extraJobs = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dloizides-jobs-{Guid.NewGuid():N}.db");
        var connectionString = $"DataSource={dbPath}";
        var time = new TestTimeProvider(start ?? DateTimeOffset.Parse("2026-08-14T09:00:00Z"));
        var probe = new JobProbe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(probe);
        services.AddDbContext<TestJobsDbContext>(o => o.UseSqlite(connectionString));

        preConfigure?.Invoke(services);

        services.AddDloizidesJobs(jobs =>
        {
            jobs.AddJob<ResumableIngestJob>();
            jobs.AddJob<CheckpointingJob>();
            jobs.AddJob<SimpleJob>();
            jobs.AddJob<WatchedJob>();
            jobs.UseEntityFrameworkStore<TestJobsDbContext>();
            extraJobs?.Invoke(jobs);
        });

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TestJobsDbContext>().Database.EnsureCreated();
        }

        return new TestHarness(provider, time, probe, dbPath);
    }

    /// <summary>A fresh runner instance — each mints its OWN lease owner id, so two of them stand in for two
    /// pods in a reclaim test.</summary>
    public JobRunnerHostedService NewRunner() =>
        ActivatorUtilities.CreateInstance<JobRunnerHostedService>(Provider);

    /// <summary>Run a delegate inside a DI scope, resolving scoped services (the store, the trigger, the
    /// status query) the way the runtime does.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> body)
    {
        using var scope = Provider.CreateScope();
        return await body(scope.ServiceProvider).ConfigureAwait(false);
    }

    /// <summary>Resolve the store in a fresh scope for a one-off store call.</summary>
    public Task<T> StoreAsync<T>(Func<IJobStore, Task<T>> body) =>
        InScopeAsync(sp => body(sp.GetRequiredService<IJobStore>()));

    /// <summary>Seed a run row directly (bypassing the trigger), for tests that need a specific history.</summary>
    public async Task SeedAsync(JobRun run)
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestJobsDbContext>();
        db.JobRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);
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
