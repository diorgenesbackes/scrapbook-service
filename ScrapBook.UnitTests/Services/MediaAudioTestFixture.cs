using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal static class MediaAudioTestFixture
{
    public const string Bucket = "private-media";
    public const string ProviderJobId = "1700000000000-test-job";
    public static Guid RequestKey { get; } = Guid.Parse("b245a7e1-e8a8-4c0a-9dbc-d1ffc123e54e");
    public static DateTimeOffset Now { get; } = MediaPreparationTestFixture.Confirmation.ConfirmedAt.AddSeconds(10);
    public static MediaAudioSettings Settings { get; } = new("123456789012", "us-east-1",
        "arn:aws:mediaconvert:us-east-1:123456789012:queues/private-audio", "arn:aws:iam::123456789012:role/audio-convert");
    public static string OutputPrefix => $"submissions/transcode-output/701/{RequestKey:N}/";
    public static string OutputKey => OutputPrefix + "playback.m4a";
    public static MediaAudioProfileModel Profile { get; } = new(Settings.Account, Settings.Region, Settings.QueueArn,
        Settings.JobRoleArn, 48000, 1, 96000);

    public static MediaJobEntity Job()
    {
        var confirmation = MediaPreparationTestFixture.Confirmation;
        return new(52, confirmation.EventId, confirmation.RecordId, confirmation.FileId, 1, 0, "pending", 0, 1,
            confirmation.MessageId, confirmation.CorrelationId, confirmation.ConfirmedAt, null, 0, "{}");
    }

    public static MediaAudioRepositoryStub InFlight(DateTimeOffset? submittedAt, string? providerJobId = null)
    {
        var attempt = new MediaAudioAttemptEntity(701, RequestKey, OutputPrefix, Now.AddMinutes(-1), submittedAt,
            providerJobId, providerJobId is null ? "unknown" : "submitted");
        var staged = Input($"submissions/transcode-input/701/{RequestKey:N}/input", 1);
        var plan = JsonSerializer.Serialize(new MediaAudioPlanModel(Settings, Bucket, staged, Profile));
        return new()
        {
            Attempt = attempt,
            Job = Job() with
            {
                State = "reconciling", ActiveAttemptId = attempt.Id, AttemptCount = 1, LastAttemptNumber = 1, Profile = plan
            }
        };
    }

    public static MediaAudioInputModel Input(string key, int channels = 1) =>
        new(new(Bucket, key, "staged-version", 2000, SHA256.HashData([1, 2, 3, 4]), Now), 10m, channels);

    public static MediaAudioVariantModel Variant(string key) =>
        new(new(Bucket, key, "output-version", 2000, SHA256.HashData([4, 3, 2, 1]), Now), 10.02m, "audio/mp4", "m4a", "aac", 1, 48000);

    public static MediaProviderJobModel ProviderJob(Guid requestKey, string state = "submitted", string? key = null) =>
        new(ProviderJobId, Settings.QueueArn, requestKey, state, key is null ? null : $"s3://{Bucket}/{key}");

    public static MediaAudioCallbackModel Callback(Guid? requestKey = null) =>
        new(Settings.Account, Settings.Region, Settings.QueueArn, ProviderJobId, requestKey ?? RequestKey, "complete");
}
