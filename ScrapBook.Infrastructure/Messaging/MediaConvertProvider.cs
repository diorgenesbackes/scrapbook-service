using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;
using Amazon.Runtime;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Messaging;

public sealed class MediaConvertProvider(IAmazonMediaConvert client) : IMediaTranscodingProvider
{
    private const string CorrelationKey = "requestKey";
    private const string AudioSelectorName = "Primary";
    private const string AudioExtension = "m4a";
    private const string VideoExtension = "mp4";
    private const int MaximumSearchPages = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SearchClockTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SubmissionWindow = TimeSpan.FromSeconds(45);

    public async Task<MediaProviderJobModel> CreateAsync(MediaAudioRequestModel request, CancellationToken cancellationToken)
    {
        var submission = CreateRequest(request);
        using var budget = CreateBudget(cancellationToken);
        try
        {
            var response = await client.CreateJobAsync(submission, budget.Token);
            var job = ReadJob(response.Job);
            return MatchesRequest(response.Job, job, request) ? job : throw new MediaProviderUnavailableException();
        }
        catch (TooManyRequestsException error)
        {
            throw new MediaJobRejectedException(true, error);
        }
        catch (Exception error) when (error is BadRequestException or ForbiddenException or NotFoundException)
        {
            throw new MediaJobRejectedException(false, error);
        }
        catch (Exception error) when (TransportFailed(error, cancellationToken))
        {
            throw new MediaProviderUnavailableException(error);
        }
    }

    public async Task<MediaProviderJobModel?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        using var budget = CreateBudget(cancellationToken);
        try
        {
            var response = await client.GetJobAsync(new GetJobRequest { Id = jobId }, budget.Token);
            var job = ReadJob(response.Job);
            return job.Id == jobId ? job : throw new MediaProviderUnavailableException();
        }
        catch (NotFoundException)
        {
            return null;
        }
        catch (Exception error) when (TransportFailed(error, cancellationToken))
        {
            throw new MediaProviderUnavailableException(error);
        }
    }

    public async Task<MediaProviderJobModel?> FindAsync(MediaAudioRequestModel request, DateTimeOffset submittedAt,
        CancellationToken cancellationToken)
    {
        using var budget = CreateBudget(cancellationToken);
        try
        {
            return await FindInQueueAsync(request, submittedAt, budget.Token);
        }
        catch (Exception error) when (TransportFailed(error, cancellationToken))
        {
            throw new MediaProviderUnavailableException(error);
        }
    }

    private async Task<MediaProviderJobModel?> FindInQueueAsync(MediaAudioRequestModel request, DateTimeOffset submittedAt,
        CancellationToken cancellationToken)
    {
        var minimum = submittedAt - SearchClockTolerance;
        var maximum = submittedAt + SubmissionWindow + SearchClockTolerance;
        var queueArn = request.Profile.QueueArn;
        var query = new ListJobsRequest
        {
            Queue = queueArn[(queueArn.LastIndexOf('/') + 1)..],
            Order = Order.DESCENDING,
            MaxResults = 20
        };
        MediaProviderJobModel? found = null;
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaximumSearchPages; page++)
        {
            var response = await client.ListJobsAsync(query, cancellationToken);
            foreach (var candidate in response.Jobs ?? [])
            {
                if (candidate.CreatedAt is not { } timestamp || timestamp == default) continue;
                var createdAt = new DateTimeOffset(timestamp.ToUniversalTime());
                if (createdAt < minimum) return found;
                if (createdAt > maximum || candidate.Queue != queueArn || ReadRequestKey(candidate) != request.RequestKey)
                    continue;

                var job = ReadJob(candidate);
                if (!MatchesRequest(candidate, job, request) || found is not null && found.Id != job.Id)
                    throw new MediaProviderUnavailableException();
                found = job;
            }

            if (response.NextToken is not { Length: > 0 } nextToken || !tokens.Add(nextToken)) return found;
            query.NextToken = nextToken;
        }

        return found;
    }

    private static CreateJobRequest CreateRequest(MediaAudioRequestModel request)
    {
        var profile = request.Profile;
        var extension = request.Video is null ? AudioExtension : VideoExtension;
        if (!request.OutputKey.EndsWith("." + extension, StringComparison.Ordinal)
            || profile.Channels is < 0 or > 2 || request.Video is null && profile.Channels == 0)
            throw new MediaJobRejectedException(false);

        var destination = S3Uri(request, request.OutputKey[..^(extension.Length + 1)]);
        return new CreateJobRequest
        {
            Queue = profile.QueueArn,
            Role = profile.JobRoleArn,
            ClientRequestToken = request.RequestKey.ToString("N"),
            UserMetadata = new Dictionary<string, string> { [CorrelationKey] = request.RequestKey.ToString("N") },
            Settings = new JobSettings
            {
                Inputs = [PlaybackInput(request)],
                OutputGroups =
                [
                    new OutputGroup
                    {
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.FILE_GROUP_SETTINGS,
                            FileGroupSettings = new FileGroupSettings { Destination = destination }
                        },
                        Outputs = [PlaybackOutput(request, extension)]
                    }
                ]
            }
        };
    }

    private static Input PlaybackInput(MediaAudioRequestModel request)
    {
        var input = new Input
        {
            FileInput = S3Uri(request, request.InputKey),
            TimecodeSource = InputTimecodeSource.ZEROBASED
        };
        if (request.Profile.Channels > 0)
        {
            input.AudioSelectors = new Dictionary<string, AudioSelector>
            {
                [AudioSelectorName] = new()
                {
                    DefaultSelection = AudioDefaultSelection.DEFAULT,
                    SelectorType = AudioSelectorType.TRACK,
                    Tracks = [1]
                }
            };
        }
        if (request.Video is { } video)
            input.VideoSelector = new VideoSelector { Rotate = InputRotation(video.Rotation) };

        return input;
    }

    private static InputRotate InputRotation(int counterClockwiseDegrees) => counterClockwiseDegrees switch
    {
        0 => InputRotate.DEGREE_0,
        90 => InputRotate.DEGREES_270,
        180 => InputRotate.DEGREES_180,
        270 => InputRotate.DEGREES_90,
        _ => throw new MediaJobRejectedException(false)
    };

    private static Output PlaybackOutput(MediaAudioRequestModel request, string extension)
    {
        var output = new Output
        {
            Extension = extension,
            ContainerSettings = new ContainerSettings
            {
                Container = ContainerType.MP4,
                Mp4Settings = new Mp4Settings { MoovPlacement = Mp4MoovPlacement.PROGRESSIVE_DOWNLOAD }
            }
        };
        if (request.Profile.Channels > 0) output.AudioDescriptions = [PlaybackAudio(request.Profile)];
        if (request.Video is { } video) output.VideoDescription = PlaybackVideo(video);

        return output;
    }

    private static AudioDescription PlaybackAudio(MediaAudioProfileModel profile) => new()
    {
        AudioSourceName = AudioSelectorName,
        CodecSettings = new AudioCodecSettings
        {
            Codec = AudioCodec.AAC,
            AacSettings = new AacSettings
            {
                CodecProfile = AacCodecProfile.LC,
                Specification = AacSpecification.MPEG4,
                RateControlMode = AacRateControlMode.CBR,
                CodingMode = profile.Channels == 1 ? AacCodingMode.CODING_MODE_1_0 : AacCodingMode.CODING_MODE_2_0,
                SampleRate = profile.SampleRate,
                Bitrate = profile.Bitrate
            }
        }
    };

    private static VideoDescription PlaybackVideo(MediaVideoProfileModel profile)
    {
        if (profile.Width is < 2 or > 1920 || profile.Height is < 2 or > 1920
            || profile.Width % 2 != 0 || profile.Height % 2 != 0
            || profile.FrameRateNumerator <= 0 || profile.FrameRateDenominator <= 0
            || profile.FrameRateNumerator > 30L * profile.FrameRateDenominator)
            throw new MediaJobRejectedException(false);

        return new VideoDescription
        {
            Width = profile.Width,
            Height = profile.Height,
            ScalingBehavior = ScalingBehavior.FIT_NO_UPSCALE,
            CodecSettings = new VideoCodecSettings
            {
                Codec = VideoCodec.H_264,
                H264Settings = new H264Settings
                {
                    CodecProfile = H264CodecProfile.MAIN,
                    RateControlMode = H264RateControlMode.QVBR,
                    QualityTuningLevel = H264QualityTuningLevel.SINGLE_PASS_HQ,
                    MaxBitrate = 5_000_000,
                    QvbrSettings = new H264QvbrSettings { QvbrQualityLevel = 7 },
                    FramerateControl = H264FramerateControl.SPECIFIED,
                    FramerateConversionAlgorithm = H264FramerateConversionAlgorithm.DUPLICATE_DROP,
                    FramerateNumerator = profile.FrameRateNumerator,
                    FramerateDenominator = profile.FrameRateDenominator,
                    ParControl = H264ParControl.SPECIFIED,
                    ParNumerator = 1,
                    ParDenominator = 1
                }
            }
        };
    }

    private static MediaProviderJobModel ReadJob(Job? job)
    {
        if (job?.Id is not { Length: > 0 } id || job.Queue is not { Length: > 0 } queue
            || ReadRequestKey(job) is not { } requestKey) throw new MediaProviderUnavailableException();
        var queueParts = queue.Split(':');
        if (queueParts is not ["arn", var partition, "mediaconvert", var region, var account, var resource]
            || !resource.StartsWith("queues/", StringComparison.Ordinal)
            || job.Arn != $"arn:{partition}:mediaconvert:{region}:{account}:jobs/{id}")
            throw new MediaProviderUnavailableException();

        var state = job.Status?.Value switch
        {
            "SUBMITTED" => "submitted",
            "PROGRESSING" => "progressing",
            "COMPLETE" => "complete",
            "ERROR" => "error",
            "CANCELED" => "canceled",
            _ => throw new MediaProviderUnavailableException()
        };
        var (destination, outputUri) = ReadOutputUris(job);
        return new MediaProviderJobModel(id, queue, requestKey, state, outputUri, job.Role, job.ClientRequestToken,
            ReadInputUri(job), destination);
    }

    private static string? ReadInputUri(Job job) => job.Settings?.Inputs switch
    {
        null => null,
        [var input] => input.FileInput,
        _ => throw new MediaProviderUnavailableException()
    };

    private static (string? Destination, string? OutputUri) ReadOutputUris(Job job)
    {
        if (job.Settings?.OutputGroups is not { } groups) return (null, null);
        if (groups is not [var group]
            || group.OutputGroupSettings?.Type is { } type && type != OutputGroupType.FILE_GROUP_SETTINGS)
            throw new MediaProviderUnavailableException();

        var destination = group.OutputGroupSettings?.FileGroupSettings?.Destination;
        if (group.Outputs is not { } outputs) return (destination, null);
        if (outputs is not [var output] || output.Extension is not (null or AudioExtension or VideoExtension)
            || !string.IsNullOrEmpty(output.NameModifier)) throw new MediaProviderUnavailableException();

        return (destination, destination is not null && output.Extension is not null ? destination + "." + output.Extension : null);
    }

    private static bool MatchesRequest(Job response, MediaProviderJobModel job, MediaAudioRequestModel request) =>
        job.QueueArn == request.Profile.QueueArn && job.RequestKey == request.RequestKey
        && response.Role == request.Profile.JobRoleArn
        && (string.IsNullOrEmpty(response.ClientRequestToken) || response.ClientRequestToken == request.RequestKey.ToString("N"));

    private static Guid? ReadRequestKey(Job job) => job.UserMetadata is { } metadata
        && metadata.TryGetValue(CorrelationKey, out var value) && Guid.TryParseExact(value, "N", out var key) && key != Guid.Empty
        && value == key.ToString("N") ? key : null;

    private static string S3Uri(MediaAudioRequestModel request, string key) => $"s3://{request.Bucket}/{key}";

    private static bool TransportFailed(Exception error, CancellationToken caller) =>
        error is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or TimeoutException
            || error is OperationCanceledException && !caller.IsCancellationRequested;

    private static CancellationTokenSource CreateBudget(CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RequestTimeout);
        return budget;
    }
}
