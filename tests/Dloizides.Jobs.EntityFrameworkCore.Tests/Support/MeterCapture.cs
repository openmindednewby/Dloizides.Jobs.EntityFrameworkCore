using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Dloizides.Jobs.Metrics;

namespace Dloizides.Jobs.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Listens to the <c>Dloizides.Jobs</c> meter the way an exporter does, keeping only measurements tagged
/// with ONE service name so parallel tests (each with its own harness and Meter) never see each other.
/// </summary>
public sealed class MeterCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly string _service;
    private readonly ConcurrentQueue<(string Instrument, string Job, double Value)> _measurements = new();

    public MeterCapture(string service)
    {
        _service = service;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == JobMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _listener.Start();
    }

    /// <summary>Every value recorded for one instrument + job, oldest first (observable gauges are polled first).</summary>
    public IReadOnlyList<double> All(string instrument, string job)
    {
        _listener.RecordObservableInstruments();
        return _measurements.Where(m => m.Instrument == instrument && m.Job == job).Select(m => m.Value).ToList();
    }

    /// <summary>The most recent value for one instrument + job, or null when none was recorded.</summary>
    public double? Latest(string instrument, string job)
    {
        var all = All(instrument, job);
        return all.Count == 0 ? null : all[^1];
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? job = null;
        string? service = null;
        foreach (var tag in tags)
        {
            if (tag.Key == JobMetricTags.Job)
            {
                job = tag.Value as string;
            }
            else if (tag.Key == JobMetricTags.Service)
            {
                service = tag.Value as string;
            }
        }

        if (job is not null && service == _service)
        {
            _measurements.Enqueue((instrument.Name, job, value));
        }
    }
}
