using Dloizides.Jobs.Configuration;
using Dloizides.Jobs.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dloizides.Jobs.EntityFrameworkCore;

/// <summary>
/// The selector for the Postgres <c>LISTEN</c>/<c>NOTIFY</c> status backplane: call inside the
/// <c>AddDloizidesJobs</c> callback, then set <c>Jobs:Status:Backplane=Postgres</c> to activate it. The
/// backplane reuses the same database as the job store (its connection string is read from the app's
/// DbContext), so opting into cross-pod PUSH needs no message broker.
/// </summary>
public static class JobsBuilderPostgresBackplaneExtensions
{
    /// <summary>
    /// Register the Postgres backplane under the <c>Postgres</c> key. The connection string is resolved
    /// lazily from <typeparamref name="TContext"/> (the same database as the job store); the channel comes
    /// from <c>Jobs:Status:Channel</c> unless <paramref name="channel"/> overrides it. The LISTEN hosted
    /// service is registered unconditionally but stays dormant unless Postgres is the selected backplane, so
    /// this call is safe to leave in place while another backplane is configured.
    /// </summary>
    /// <typeparam name="TContext">The app's DbContext — its provider must be Npgsql.</typeparam>
    /// <param name="builder">The jobs builder.</param>
    /// <param name="channel">Optional channel override; defaults to <c>Jobs:Status:Channel</c>.</param>
    public static JobsBuilder UsePostgresStatusBackplane<TContext>(this JobsBuilder builder, string? channel = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(sp => new PostgresListenNotifyBackplane(
            connectionStringProvider: () =>
            {
                using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var connectionString = scope.ServiceProvider.GetRequiredService<TContext>().Database.GetConnectionString();
                return connectionString ?? throw new InvalidOperationException(
                    $"UsePostgresStatusBackplane<{typeof(TContext).Name}> could not read a connection string from "
                    + "the DbContext. The Postgres status backplane needs a raw Npgsql connection to LISTEN/NOTIFY.");
            },
            options: sp.GetRequiredService<IOptions<JobsOptions>>(),
            channelOverride: channel,
            logger: sp.GetRequiredService<ILogger<PostgresListenNotifyBackplane>>()));

        builder.AddStatusBackplane(
            JobStatusBackplanes.Postgres, sp => sp.GetRequiredService<PostgresListenNotifyBackplane>());

        builder.Services.AddHostedService(sp => sp.GetRequiredService<PostgresListenNotifyBackplane>());
        return builder;
    }
}
