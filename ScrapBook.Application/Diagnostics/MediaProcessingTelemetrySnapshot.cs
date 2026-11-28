namespace ScrapBook.Application.Diagnostics;

public sealed record MediaProcessingTelemetrySnapshot(string Stage, string MediaType, string Cohort, string Outcome,
    long SampleCount, long DurationSampleCount, double DurationSumSeconds, double DurationMaximumSeconds,
    long WithinTargetCount, IReadOnlyDictionary<string, long> DurationBuckets, string? FailureStage);
