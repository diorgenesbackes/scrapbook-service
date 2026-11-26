using System.Diagnostics.Metrics;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Diagnostics;

public static class EventStructuralLockTelemetry
{
    private static readonly Meter Meter = new("ScrapBook.EventStructuralLock");
    private static readonly Histogram<double> LockDuration = Meter.CreateHistogram<double>("scrapbook.event.structural_lock.duration", "s");
    private static readonly Counter<long> Redeliveries = Meter.CreateCounter<long>("scrapbook.event.structural_lock.redeliveries");
    private static readonly Counter<long> Publications = Meter.CreateCounter<long>("scrapbook.event.structural_lock.publications");
    private static readonly Counter<long> Consumptions = Meter.CreateCounter<long>("scrapbook.event.structural_lock.consumptions");

    private static readonly Gauge<long> Pending = Meter.CreateGauge<long>("scrapbook.event.structural_lock.outbox.pending");
    private static readonly Gauge<long> Failed = Meter.CreateGauge<long>("scrapbook.event.structural_lock.outbox.failed");
    private static readonly Gauge<double> Oldest = Meter.CreateGauge<double>("scrapbook.event.structural_lock.outbox.oldest_pending", "s");

    private static readonly object IntervalLock = new();
    private static readonly OutboxPublishResult[] PublicationResults = Enum.GetValues<OutboxPublishResult>();
    private static readonly EventStructuralLockResult[] ConsumptionResults = Enum.GetValues<EventStructuralLockResult>();
    private static readonly Dictionary<OutboxPublishResult, long> PublicationCounts = PublicationResults.ToDictionary(result => result, _ => 0L);
    private static readonly Dictionary<EventStructuralLockResult, long> ConsumptionCounts = ConsumptionResults.ToDictionary(result => result, _ => 0L);
    private static long consumptionRetries;
    private static long redeliveryCount;
    private static long durationCount;
    private static double durationSum;
    private static double durationMaximum;
    private static long durationOverTarget;

    public static void RecordBacklog(long pending, long failed, double oldestSeconds)
    {
        Pending.Record(pending);
        Failed.Record(failed);
        Oldest.Record(oldestSeconds);
    }

    public static void RecordLock(double seconds)
    {
        var duration = Math.Max(0, seconds);
        lock (IntervalLock)
        {
            durationCount++;
            durationSum += duration;
            durationMaximum = Math.Max(durationMaximum, duration);
            if (duration > 5)
            {
                durationOverTarget++;
            }
        }
        LockDuration.Record(duration);
    }

    public static void RecordRedelivery()
    {
        lock (IntervalLock)
        {
            redeliveryCount++;
        }
        Redeliveries.Add(1);
    }

    public static void RecordPublication(OutboxPublishResult outcome)
    {
        lock (IntervalLock)
        {
            PublicationCounts[outcome]++;
        }
        Publications.Add(1, new KeyValuePair<string, object?>("outcome", outcome.ToString()));
    }

    public static void RecordConsumption(EventStructuralLockResult? outcome)
    {
        lock (IntervalLock)
        {
            if (outcome is { } result)
            {
                ConsumptionCounts[result]++;
            }
            else
            {
                consumptionRetries++;
            }
        }
        Consumptions.Add(1, new KeyValuePair<string, object?>("outcome", outcome?.ToString() ?? "Retry"));
    }

    public static EventStructuralLockTelemetrySnapshot CollectInterval()
    {
        lock (IntervalLock)
        {
            var publications = PublicationResults.ToDictionary(result => result.ToString(), result => PublicationCounts[result]);
            var consumptions = ConsumptionResults.ToDictionary(result => result.ToString(), result => ConsumptionCounts[result]);
            consumptions["Retry"] = consumptionRetries;
            var average = durationCount == 0 ? 0 : durationSum / durationCount;
            var snapshot = new EventStructuralLockTelemetrySnapshot(publications, consumptions, redeliveryCount,
                durationCount, average, durationMaximum, durationOverTarget);

            foreach (var outcome in PublicationResults) PublicationCounts[outcome] = 0;
            foreach (var outcome in ConsumptionResults) ConsumptionCounts[outcome] = 0;
            consumptionRetries = 0;
            redeliveryCount = 0;
            durationCount = 0;
            durationSum = 0;
            durationMaximum = 0;
            durationOverTarget = 0;
            return snapshot;
        }
    }
}
