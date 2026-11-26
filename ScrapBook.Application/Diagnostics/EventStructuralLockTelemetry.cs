using System.Diagnostics.Metrics;

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

    public static void RecordBacklog(long pending, long failed, double oldestSeconds)
    {
        Pending.Record(pending);
        Failed.Record(failed);
        Oldest.Record(oldestSeconds);
    }

    public static void RecordLock(double seconds) => LockDuration.Record(Math.Max(0, seconds));
    public static void RecordRedelivery() => Redeliveries.Add(1);
    public static void RecordPublication(string outcome) => Publications.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public static void RecordConsumption(string outcome) => Consumptions.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
