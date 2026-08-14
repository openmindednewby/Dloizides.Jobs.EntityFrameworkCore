using System.Text.RegularExpressions;
using Dloizides.Jobs.Abstractions;
using Dloizides.Jobs.Backplane;
using Dloizides.Jobs.Configuration;
using Dloizides.Jobs.Runtime;
using Dloizides.Jobs.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dloizides.Jobs.EntityFrameworkCore;

/// <summary>
/// A cross-pod status backplane over Postgres <c>LISTEN</c>/<c>NOTIFY</c> — real-time PUSH with NO new infra,
/// because it reuses the database the job store already runs on. The publishing pod issues
/// <c>pg_notify(channel, payload)</c>; every pod holds ONE dedicated <c>LISTEN</c> connection and re-emits
/// each arriving <see cref="JobStatusEvent"/> to its local subscribers (a wire connection subscribes to be
/// told "job X changed", then re-reads the durable <see cref="JobStatus"/>).
/// </summary>
/// <remarks>
/// <para>
/// PUSH IS NEVER LOAD-BEARING. <c>NOTIFY</c> is delivered only to connections currently listening and is not
/// durable, so a notification lost to a reconnect is simply healed by the next poll — exactly the design's
/// persist-first-push-second contract. Publishing fails soft; the <c>LISTEN</c> loop reconnects with backoff.
/// </para>
/// <para>
/// SCALE CEILING. <c>LISTEN</c>/<c>NOTIFY</c> comfortably serves tens of pods each fanning out to thousands
/// of clients; every pod holds one extra connection. When that fan-out becomes the bottleneck, swap
/// <c>Jobs:Status:Backplane</c> to a broker-backed transport (e.g. Redis) — the seam is unchanged.
/// </para>
/// </remarks>
public sealed class PostgresListenNotifyBackplane : BackgroundService, IJobStatusBackplane
{
    // A Postgres channel is an identifier and cannot be parameterised in LISTEN, so it is validated rather
    // than interpolated blind — this is the one place the channel string reaches SQL as an identifier.
    private static readonly Regex SafeChannel = new("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly Func<string> _connectionStringProvider;
    private readonly string _channel;
    private readonly string _configuredBackplane;
    private readonly InMemoryJobStatusBus _local = new();
    private readonly ILogger<PostgresListenNotifyBackplane> _logger;

    /// <summary>Construct the backplane. The connection string is resolved lazily (from the app's DbContext),
    /// so nothing connects until the LISTEN loop starts or the first publish fires.</summary>
    /// <param name="connectionStringProvider">Yields the Postgres connection string — same database as the
    /// job store, so publisher and listeners share one <c>NOTIFY</c> namespace.</param>
    /// <param name="options">Jobs options; supplies the channel and the selected-backplane guard.</param>
    /// <param name="channelOverride">Optional explicit channel, overriding <c>Jobs:Status:Channel</c>.</param>
    /// <param name="logger">Logger for reconnects and swallowed publish faults.</param>
    public PostgresListenNotifyBackplane(
        Func<string> connectionStringProvider,
        IOptions<JobsOptions> options,
        string? channelOverride,
        ILogger<PostgresListenNotifyBackplane> logger)
    {
        _connectionStringProvider = connectionStringProvider;
        var status = options.Value.Status;
        _channel = channelOverride ?? status.Channel;
        _configuredBackplane = status.Backplane;
        _logger = logger;

        if (!SafeChannel.IsMatch(_channel))
        {
            throw new ArgumentException(
                $"Jobs:Status:Channel '{_channel}' is not a valid Postgres channel identifier "
                + "(letters, digits and underscore; not starting with a digit).", nameof(options));
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(JobStatusEvent evt, CancellationToken cancellationToken)
    {
        var payload = JobJson.Serialize(evt);

        // A pooled short-lived connection: pg_notify takes the channel as a TEXT argument, so publishing is
        // fully parameterised (no identifier interpolation on the hot path).
        await using var connection = new NpgsqlConnection(_connectionStringProvider());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
        command.Parameters.AddWithValue("channel", _channel);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<JobStatusEvent> handler) => _local.Subscribe(handler);

    /// <summary>
    /// Hold one <c>LISTEN</c> connection for the process lifetime, re-emitting each notification to local
    /// subscribers. Registered as a hosted service by <c>UsePostgresStatusBackplane</c>; it no-ops unless
    /// <c>Jobs:Status:Backplane</c> actually selected Postgres, so registering it is harmless when another
    /// backplane is chosen.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(_configuredBackplane, JobStatusBackplanes.Postgres, StringComparison.OrdinalIgnoreCase))
        {
            // A different backplane is selected; this instance is dormant and never opens a LISTEN connection.
            return;
        }

        var delay = MinReconnectDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenLoopAsync(stoppingToken).ConfigureAwait(false);
                delay = MinReconnectDelay; // A clean return (only on shutdown) needs no backoff reset drama.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Job status LISTEN connection on channel '{Channel}' dropped; reconnecting in {Delay}.",
                    _channel, delay);
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = delay < MaxReconnectDelay ? delay + delay : MaxReconnectDelay;
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_connectionStringProvider());
        await connection.OpenAsync(stoppingToken).ConfigureAwait(false);

        connection.Notification += OnNotification;
        try
        {
            // The channel is validated in the constructor, so this identifier interpolation is safe.
            await using (var listen = new NpgsqlCommand($"LISTEN {_channel}", connection))
            {
                await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Job status backplane listening on Postgres channel '{Channel}'.", _channel);

            // WaitAsync blocks until a notification arrives (or the token trips), dispatching it to the
            // Notification handler. This connection does nothing else — it exists only to carry NOTIFYs.
            while (!stoppingToken.IsCancellationRequested)
            {
                await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connection.Notification -= OnNotification;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        var evt = JobJson.Deserialize<JobStatusEvent>(e.Payload);
        if (evt is not null)
        {
            _local.Publish(evt);
        }
    }
}
