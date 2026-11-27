using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal static class MediaVideoTestFixture
{
    public static MediaAudioRepositoryStub Pending() => new()
    {
        Job = MediaAudioTestFixture.Job() with { FileKind = "video" },
        Source = MediaPreparationTestFixture.Source() with { Kind = "video" }
    };

    public static MediaVideoPropertiesModel SourceProperties { get; } =
        new("mov", "h264", "aac", 10m, 1280, 720, 1, 1, 30, 1, 0, false, 2, 44100, false, "LC");

    public static MediaVideoInputModel Input(string key, MediaVideoPropertiesModel? properties = null) =>
        new(MediaAudioTestFixture.Input(key).Object, properties ?? SourceProperties);

    public static MediaAudioVariantModel Variant(string key, int channels = 2)
    {
        var video = SourceProperties with
        {
            Container = "mp4",
            AudioCodec = channels == 0 ? null : "aac",
            AudioChannels = channels,
            AudioSampleRate = channels == 0 ? 0 : 48000,
            AudioProfile = channels == 0 ? null : "LC"
        };
        return MediaAudioTestFixture.Variant(key) with
        {
            Duration = video.Duration,
            MediaType = "video/mp4",
            Container = video.Container,
            AudioCodec = video.AudioCodec,
            Channels = video.AudioChannels,
            SampleRate = video.AudioSampleRate,
            Video = video
        };
    }

    public static MediaProviderJobModel Completed(MediaAudioRequestModel request) =>
        MediaAudioTestFixture.ProviderJob(request.RequestKey, "complete", request.OutputKey) with
        {
            JobRoleArn = request.Profile.JobRoleArn,
            ClientRequestToken = request.RequestKey.ToString("N"),
            InputUri = $"s3://{request.Bucket}/{request.InputKey}",
            OutputDestination = $"s3://{request.Bucket}/{request.OutputKey[..^4]}"
        };
}
