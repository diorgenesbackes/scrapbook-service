namespace ScrapBook.Application.Diagnostics;

public sealed record EventStructuralLockTelemetrySnapshot(IReadOnlyDictionary<string, long> PublicationOutcomes,
    IReadOnlyDictionary<string, long> ConsumptionOutcomes, long Redeliveries, long LockDurationSamples,
    double AverageLockDurationSeconds, double MaximumLockDurationSeconds, long LockDurationOverTargetCount);
