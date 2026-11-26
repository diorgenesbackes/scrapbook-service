using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;
using ScrapBook.Infrastructure.Codecs;

namespace ScrapBook.UnitTests.Services;

public sealed class VideoSubmissionServiceTests
{
    private const int PartSize = 16 * 1024 * 1024;
    private const int MultipartThreshold = 2 * PartSize;
    private const int MultipartSize = MultipartThreshold + 7;
    private const long RecordId = 19;
    private const string Bucket = "lab-videos";
    private const string Temporary = "submissions/temporary/video";
    private const string Original = "submissions/original/video";
    private const string UploadId = "multipart-upload";
    private const string Version = "completed-version";
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly byte[] Sha256 = SHA256.HashData(Bytes);
    private static readonly byte[] BadSha256 = SHA256.HashData([4, 3, 2, 1]);
    private static readonly byte[][] PartChecksums = Enumerable.Range(1, 30)
        .Select(number => SHA256.HashData([(byte)number])).ToArray();
    private static readonly DateTimeOffset AdmittedAt = DateTimeOffset.Parse("2032-03-01T14:59:59Z");
    private static readonly DateTimeOffset Deadline = DateTimeOffset.Parse("2032-03-01T16:00:00Z");
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly Guid attemptKey = Guid.NewGuid();
    private readonly Guid fileKey = Guid.NewGuid();
    private readonly byte[] secret = new byte[32];
    private readonly SubmissionProtectionCodec codec = new("local", new Dictionary<string, byte[]> { ["local"] = new byte[32] });
    private readonly SubmissionTestRepository submissions = new();
    private readonly EventTestTimeProvider clock = new() { Now = AdmittedAt };
    private readonly PhotoSubmissionStorageStub photosStorage = new(Bytes);
    private readonly PhotoSubmissionDecoderStub photosDecoder = new();
    private readonly VideoSubmissionStorageStub videoStorage = new(Sha256);
    private readonly VideoSubmissionDecoderStub videoDecoder = new(
        new("video/mp4", "mp4", "h264", "aac", 60m, 320, 240, 24, 1, null, Sha256));
    private readonly PhotoSubmissionRepositoryStub photos;
    private readonly VideoSubmissionRepositoryStub videos;

    public VideoSubmissionServiceTests()
    {
        photos = new(submissions.Event);
        videos = new(photos);
    }

    private PhotoSubmissionService Media(PhotoSubmissionSettings? settings = null) => new(photos, submissions, photosStorage,
        photosDecoder, settings ?? new(true, Bucket, VideoEnabled: true), clock,
        video: new(photos, videos, videoStorage, videoDecoder, clock));

    private SubmissionService Submissions => new(submissions, codec, clock, Media());

    [Theory]
    [InlineData("moment", "video/mp4")]
    [InlineData("testimonial", "video/quicktime")]
    [InlineData("testimonial", "video/webm")]
    public async Task AdmitsOneSupportedVideoAtTheInclusiveSizeAndCaptionLimitsAsync(string category, string mediaType)
    {
        var caption = string.Concat(Enumerable.Repeat("🌅", 500));
        var result = await CreateAsync(Request([File(mediaType, 500_000_000)], category, caption));

        Assert.True(result.Created);
        Assert.Equal("receiving", result.Status.State);
        Assert.Equal(Deadline, result.Status.CompletionDeadlineAt);
        Assert.Null(result.Status.ConfirmedAt);
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        Assert.Equal(("video", category, caption), (admitted.Format, admitted.Category, admitted.Caption));
        var intent = Assert.Single(admitted.Files);
        Assert.Equal(1, intent.Position);
        Assert.Equal((fileKey, mediaType, 500_000_000),
            (intent.Manifest.FileKey, intent.Manifest.DeclaredMediaType, intent.Manifest.SizeBytes));
        Assert.Equal(Sha256, intent.Manifest.Sha256);
        Assert.Equal(PartChecksums, intent.Manifest.PartSha256);
        Assert.True(submissions.Transaction.Committed);
        Assert.Empty(submissions.OutboxMessages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500_000_001)]
    public async Task RejectsVideoSizeOutsideTheInclusiveRangeAsync(int size) =>
        await AssertInvalidAsync(Request([File(size: size)]), "files[0].sizeBytes");

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task RejectsAnyManifestOtherThanOneVideoAtPositionOneAsync(int count, int position)
    {
        var files = Enumerable.Range(0, count).Select(_ => File() with { Position = position }).ToArray();

        await AssertInvalidAsync(Request(files), "files");
    }

    [Fact]
    public async Task RejectsAnUnsupportedMediaTypeAsync() =>
        await AssertInvalidAsync(Request([File("image/png")]), "files[0].declaredMediaType");

    [Fact]
    public async Task RejectsCaptionAbove500UnicodeScalarsAsync()
    {
        var caption = string.Concat(Enumerable.Repeat("🌅", 501));

        await AssertInvalidAsync(Request([File()], caption: caption), "caption");
    }

    [Theory]
    [InlineData(MultipartThreshold - 1, 0, "put")]
    [InlineData(MultipartThreshold, 2, "multipart")]
    [InlineData(MultipartThreshold + 1, 3, "multipart")]
    public async Task SelectsPutOrFixedSizeMultipartAtTheThresholdAsync(int size, int parts, string mode)
    {
        await CreateAsync(Request([File(size: size)]));
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        var manifest = Assert.Single(admitted.Files).Manifest;
        SetAttempt(admitted);
        photos.Submission = Video(size: size, partChecksums: manifest.PartSha256);

        var status = await Submissions.GetUploadStatusAsync(SubmissionTestRepository.Code, attemptKey, fileKey, secret, Token);

        Assert.Equal(mode, status.Mode);
        Assert.Equal(Deadline, status.CompletionDeadlineAt);
        Assert.Empty(status.UploadedParts);
        if (parts == 0)
        {
            Assert.Null(manifest.PartSha256);
            Assert.Null(status.PartSizeBytes);
        }
        else
        {
            Assert.Equal(PartChecksums.Take(parts), manifest.PartSha256);
            Assert.Equal(PartSize, status.PartSizeBytes);
        }
    }

    [Theory]
    [InlineData(MultipartThreshold - 1, "null", "files[0].partSha256")]
    [InlineData(MultipartThreshold, "null", "files[0].partSha256")]
    [InlineData(MultipartThreshold, "[]", "files[0].partSha256")]
    [InlineData(MultipartThreshold, "[42, 42]", "files[0].partSha256[0]")]
    [InlineData(MultipartThreshold, "[\"not-a-checksum\", null]", "files[0].partSha256[0]")]
    public async Task RejectsAbsentUnexpectedOrMalformedPartChecksumsAsync(int size, string value, string field)
    {
        var file = File(size: size) with { PartSha256 = JsonSerializer.Deserialize<JsonElement>(value) };

        await AssertInvalidAsync(Request([file]), field);
    }

    [Fact]
    public async Task RejectsMultipartChecksumsForPhotosAsync()
    {
        var photo = File("image/png") with { PartSha256 = PartManifest(1) };
        await AssertInvalidAsync(Request([photo]) with { Format = "photo" }, "files[0].partSha256");
    }

    [Fact]
    public async Task RejectsAdmissionWhenVideoIsDisabledByDefaultAsync()
    {
        var service = new SubmissionService(submissions, codec, clock, Media(new(true, Bucket)));
        await Assert.ThrowsAsync<VideoSubmissionDisabledException>(() => service.CreateAsync(SubmissionTestRepository.Code,
            attemptKey, secret, Request([File()]), AdmittedAt, AdmittedAt, Guid.NewGuid(), Token));
        Assert.Null(photos.Admitted);
    }

    [Fact]
    public async Task ResumesTheSameAttemptAndAuthorizesOnlyUnrecognizedPartsWithTheirExactLengthsAndChecksumsAsync()
    {
        var request = Request([File(size: MultipartSize)]);
        var created = await CreateAsync(request);
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        SetAttempt(admitted);
        photos.Submission = Video(MultipartSize, PartChecksums[..3], UploadId);
        videoStorage.Parts = [Part(3, 6), Part(2, PartSize, BadSha256), Part(1, PartSize)];
        clock.Now = Deadline.AddMinutes(-2);

        var replay = await CreateAsync(request, clock.Now);
        var upload = await Submissions.GetUploadStatusAsync(SubmissionTestRepository.Code, attemptKey, fileKey, secret, Token);

        Assert.False(replay.Created);
        Assert.Equal(created.Status, replay.Status);
        Assert.Same(admitted, photos.Admitted);
        Assert.Equal([1], upload.UploadedParts);
        var changedPartChecksums = JsonSerializer.SerializeToElement(new[] { BadSha256, PartChecksums[1], PartChecksums[2] }
            .Select(checksum => Convert.ToBase64String(checksum)));
        var changed = Request([File(size: MultipartSize) with { PartSha256 = changedPartChecksums }]);
        await Assert.ThrowsAsync<SubmissionKeyReusedException>(() => CreateAsync(changed, clock.Now));
        await Assert.ThrowsAsync<VideoUploadUnavailableException>(() => AuthorizeAsync(1));
        await Assert.ThrowsAsync<SubmissionAttemptNotFoundException>(() => Submissions.GetUploadStatusAsync(
            SubmissionTestRepository.Code, attemptKey, fileKey, BadSha256, Token));

        var firstPending = await AuthorizeAsync(2);
        var lastPending = await AuthorizeAsync(3);

        Assert.Equal(Deadline, firstPending.ExpiresAt);
        Assert.Equal(Deadline, lastPending.ExpiresAt);
        Assert.Collection(videoStorage.Grants,
            grant => AssertGrant(grant, 2, PartSize),
            grant => AssertGrant(grant, 3, 7));
        Assert.Empty(videoStorage.CreatedUploads);
        Assert.Empty(photosStorage.Authorizations);
    }

    [Fact]
    public async Task BindsSinglePutToTheManifestAndStopsAuthorizingOnceTheVersionIsRecognizedAsync()
    {
        await CreateAsync(Request([File()]));
        SetAttempt(Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted));
        photos.Submission = Video();

        await Assert.ThrowsAsync<EventRequestValidationException>(() => AuthorizeAsync(1));
        var result = await AuthorizeAsync(null);

        var grant = Assert.Single(photosStorage.Authorizations);
        Assert.Equal((Bucket, Temporary, "video/mp4", Bytes.Length), (grant.Bucket, grant.Key, grant.MediaType, grant.Size));
        Assert.Equal(Sha256, grant.Hash);
        Assert.Equal(AdmittedAt.AddMinutes(10), result.ExpiresAt);
        Assert.Empty(videoStorage.Grants);

        photos.Submission = Video(received: Receipt(Version, AdmittedAt, "put", Bytes.Length));
        var status = await Submissions.GetUploadStatusAsync(SubmissionTestRepository.Code, attemptKey, fileKey, secret, Token);
        Assert.Equal([1], status.UploadedParts);
        await Assert.ThrowsAsync<VideoUploadUnavailableException>(() => AuthorizeAsync(null));
        Assert.Single(photosStorage.Authorizations);
    }

    [Fact]
    public async Task ReconcilesExistingMultipartUploadBeforeTryingToCreateAnotherAsync()
    {
        await CreateAsync(Request([File(size: MultipartSize)]));
        SetAttempt(Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted));
        var file = Video(MultipartSize, PartChecksums[..3]);
        photos.Submission = file;
        videoStorage.OpenUploadIds = [UploadId];

        await AuthorizeAsync(2);

        Assert.Equal((file.TemporaryObjectId, file.SessionId), Assert.Single(videos.InitializationClaims));
        var saved = Assert.Single(videos.SavedUploads);
        Assert.Equal((file.TemporaryObjectId, file.SessionId, UploadId), (saved.ObjectId, saved.SessionId, saved.UploadId));
        Assert.Equal(UploadId, Assert.Single(videoStorage.Grants).UploadId);
        Assert.Empty(videoStorage.CreatedUploads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(4)]
    public async Task RejectsPartNumberOutsideTheManifestBeforeCreatingUploadAsync(int? partNumber)
    {
        await CreateAsync(Request([File(size: MultipartSize)]));
        SetAttempt(Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted));
        photos.Submission = Video(MultipartSize, PartChecksums[..3]);

        var error = await Assert.ThrowsAsync<EventRequestValidationException>(() => AuthorizeAsync(partNumber));

        Assert.Equal("partNumber", error.Field);
        Assert.Empty(videos.InitializationClaims);
        Assert.Empty(videoStorage.Grants);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("size")]
    [InlineData("checksum")]
    public async Task DoesNotCompleteMultipartFromUnmatchedProviderPartsAsync(string problem)
    {
        await SetMultipartAttemptAsync();
        videoStorage.Parts = problem switch
        {
            "missing" => [Part(1, PartSize), Part(2, PartSize)],
            "duplicate" => [Part(1, PartSize), Part(2, PartSize), Part(2, PartSize)],
            "size" => [Part(1, PartSize), Part(2, PartSize), Part(3, 6)],
            "checksum" => [Part(1, PartSize), Part(2, PartSize), Part(3, 7, BadSha256)],
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        var result = await SignalAsync();

        Assert.Equal("receiving", result.State);
        Assert.Null(result.ConfirmedAt);
        Assert.Empty(videoStorage.Completions);
        Assert.Empty(photos.Confirmations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Version)]
    public async Task CompletesOnlyThePlannedOrderedPartsAndWaitsForTheExactTrustedVersionAsync(string? completionVersion)
    {
        await SetMultipartAttemptAsync();
        videoStorage.Parts = [Part(3, 7), Part(2, PartSize), Part(1, PartSize)];
        videoStorage.CompletedVersion = completionVersion;

        var result = await SignalAsync();

        Assert.Equal("receiving", result.State);
        Assert.Null(result.ConfirmedAt);
        var completed = Assert.Single(videoStorage.Completions);
        Assert.Equal((Bucket, Temporary, UploadId), (completed.Bucket, completed.Key, completed.UploadId));
        Assert.Equal([1, 2, 3], completed.Parts.Select(part => part.PartNumber));
        Assert.Empty(photos.Pinned);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);

        videoStorage.Parts = null;
        var repeated = await SignalAsync();
        Assert.Equal("receiving", repeated.State);
        Assert.Single(videoStorage.Completions);

        photosStorage.UnprovenVersion = Version;
        clock.Now = Deadline.AddSeconds(2);
        Assert.Equal("reconciling", (await SignalAsync()).State);
        Assert.True(await Media().ProcessNextAsync(Token));
        Assert.Equal((clock.Now.AddHours(1), "video-receipt-missing"), photos.Scheduled);
        Assert.Null(photos.Terminated);

        await Media().RecordReceiptAsync(Receipt("another-version", Deadline, "put"), Token);
        Assert.Empty(photos.Pinned);

        var receipt = Receipt(Version, Deadline);
        await Media().RecordReceiptAsync(receipt, Token);
        Assert.Equal(receipt, Assert.Single(photos.Pinned));
        Assert.True(Assert.Single(photos.Seals));
        Assert.Empty(photos.Confirmations);

        photos.Submission = Video(MultipartSize, PartChecksums[..3], UploadId, received: receipt);
        await Media().RecordReceiptAsync(receipt, Token);
        await Media().RecordReceiptAsync(Receipt("newer-version", Deadline.AddTicks(1)), Token);
        Assert.Equal(receipt, Assert.Single(photos.Pinned));

        photosStorage.CopySize = MultipartSize;
        Assert.True(await Media().ProcessNextAsync(Token));
        var confirmation = Assert.Single(photos.Confirmations);
        Assert.Equal(submissions.Event.CompletionDeadlineAt, confirmation.ReceivedAt);
        Assert.Equal((Bucket, Temporary, Version, (long)MultipartSize), Assert.Single(videoDecoder.Reads));
        Assert.Equal((Temporary, Version, Original), Assert.Single(photosStorage.Copies));
        Assert.Equal((Bucket, Original, "original-version", (long)MultipartSize), Assert.Single(videoStorage.OriginalReads));
        Assert.Equal(2, submissions.OutboxMessages.Count);
    }

    [Fact]
    public async Task DoesNotRequestMultipartCompletionOrIssueNewPartAuthorizationAtTheCutoffAsync()
    {
        await SetMultipartAttemptAsync();
        videoStorage.Parts = [Part(1, PartSize), Part(2, PartSize), Part(3, 7)];
        clock.Now = Deadline;

        var result = await SignalAsync();

        Assert.Equal("reconciling", result.State);
        Assert.Null(result.ConfirmedAt);
        await Assert.ThrowsAsync<SubmissionDeadlineExceededException>(() => AuthorizeAsync(3));
        Assert.Empty(videoStorage.Completions);
        Assert.Empty(videoStorage.Grants);
    }

    [Fact]
    public async Task RejectsAMultipartVersionProvenToHaveArrivedOneTickAfterTheDeadlineAsync()
    {
        await SetMultipartAttemptAsync();
        clock.Now = Deadline.AddMinutes(2);
        photosStorage.UnprovenVersion = Version;

        await Media().RecordReceiptAsync(Receipt(Version, Deadline.AddTicks(1)), Token);
        Assert.True(await Media().ProcessNextAsync(Token));

        Assert.Empty(photos.Pinned);
        Assert.Equal(("rejected", "submission-deadline-exceeded"), photos.Terminated);
        Assert.Empty(videoDecoder.Reads);
        Assert.Empty(photosStorage.Copies);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
    }

    [Theory]
    [InlineData("moment", 60_000_000, true)]
    [InlineData("moment", 60_000_001, false)]
    [InlineData("testimonial", 300_000_000, true)]
    [InlineData("testimonial", 300_000_001, false)]
    [InlineData("testimonial", 1, true)]
    [InlineData("testimonial", 0, false)]
    public async Task ConfirmsOnlyVideosWithinTheInclusiveCategoryDurationLimitAsync(string category, int microseconds,
        bool expectedConfirmation)
    {
        photos.Submission = Video(received: Receipt(Version, Deadline, "put", Bytes.Length), category: category);
        videoDecoder.Metadata = videoDecoder.Metadata with { Duration = microseconds / 1_000_000m };
        clock.Now = Deadline.AddMinutes(1);

        Assert.True(await Media().ProcessNextAsync(Token));

        if (expectedConfirmation)
        {
            var confirmation = Assert.Single(photos.Confirmations);
            var confirmedFile = Assert.Single(confirmation.Files);
            Assert.Equal(videoDecoder.Metadata, confirmedFile.Video);
            Assert.Null(confirmedFile.Image);
            Assert.True(submissions.Transaction.Committed);
        }
        else
        {
            Assert.Equal(("rejected", "video-invalid"), photos.Terminated);
            Assert.Empty(photosStorage.Copies);
            Assert.Empty(photos.Confirmations);
            Assert.Empty(submissions.OutboxMessages);
        }
        Assert.Equal((Bucket, Temporary, Version, (long)Bytes.Length), Assert.Single(videoDecoder.Reads));
        Assert.Equal(0, photosDecoder.Calls);
    }

    [Fact]
    public async Task RejectsContentWithAVideoContainerDifferentFromTheManifestAsync()
    {
        photos.Submission = Video(received: Receipt(Version, Deadline, "put", Bytes.Length));
        videoDecoder.Metadata = videoDecoder.Metadata with { MediaType = "video/webm", Container = "webm" };

        Assert.True(await Media().ProcessNextAsync(Token));

        Assert.Equal(("rejected", "video-invalid"), photos.Terminated);
        Assert.Empty(photosStorage.Copies);
        Assert.Empty(photos.Confirmations);
    }

    [Fact]
    public async Task RejectsTheWrongWholeChecksumDespiteAllMultipartChecksumsMatchingAsync()
    {
        await SetMultipartAttemptAsync();
        videoStorage.Parts = [Part(1, PartSize), Part(2, PartSize), Part(3, 7)];
        Assert.Equal("receiving", (await SignalAsync()).State);
        Assert.Single(videoStorage.Completions);

        var receipt = Receipt(Version, Deadline);
        clock.Now = Deadline.AddSeconds(1);
        await Media().RecordReceiptAsync(receipt, Token);
        Assert.Equal(receipt, Assert.Single(photos.Pinned));
        photos.Submission = Video(MultipartSize, PartChecksums[..3], UploadId, received: receipt);
        videoDecoder.Metadata = videoDecoder.Metadata with { Sha256 = BadSha256 };

        Assert.True(await Media().ProcessNextAsync(Token));

        Assert.Equal(("rejected", "video-invalid"), photos.Terminated);
        Assert.Empty(photosStorage.Copies);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
    }

    [Fact]
    public async Task KeepsConfirmationPendingWhenThePreservedOriginalFailsChecksumVerificationAsync()
    {
        photos.Submission = Video(received: Receipt(Version, Deadline, "put", Bytes.Length));
        videoStorage.OriginalSha256 = BadSha256;

        Assert.True(await Media().ProcessNextAsync(Token));

        Assert.Equal((clock.Now.AddSeconds(30), "video-storage-unavailable"), photos.Scheduled);
        Assert.Null(photos.Terminated);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
        Assert.False(submissions.Transaction.Committed);

        Assert.Equal((Bucket, Original, "original-version", (long)Bytes.Length), Assert.Single(videoStorage.OriginalReads));
    }

    [Fact]
    public async Task ReusesTheSameVerifiedOriginalWhenConfirmationHasToRetryAsync()
    {
        photos.Submission = Video(received: Receipt(Version, Deadline, "put", Bytes.Length));
        photos.ConfirmationAccepted = false;

        Assert.True(await Media().ProcessNextAsync(Token));
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
        Assert.False(submissions.Transaction.Committed);

        photos.ConfirmationAccepted = true;
        Assert.True(await Media().ProcessNextAsync(Token));

        Assert.Equal((Temporary, Version, Original), Assert.Single(photosStorage.Copies));
        var originalRead = (Bucket, Original, "original-version", (long)Bytes.Length);
        Assert.Equal([originalRead, originalRead], videoStorage.OriginalReads);
        Assert.Single(photos.Confirmations);
        Assert.True(submissions.Transaction.Committed);
    }

    private PhotoSubmissionFileRequest File(string mediaType = "video/mp4", int size = 4) =>
        new(fileKey, 1, mediaType, size, Convert.ToBase64String(Sha256),
            size is >= MultipartThreshold and <= 500_000_000 ? PartManifest((size + PartSize - 1) / PartSize) : default);

    private static JsonElement PartManifest(int count) =>
        JsonSerializer.SerializeToElement(PartChecksums.Take(count).Select(checksum => Convert.ToBase64String(checksum)));

    private CreateSubmissionRequest Request(IReadOnlyList<PhotoSubmissionFileRequest?> files, string category = "moment",
        string? caption = null) => new(category, "video", new SubmissionIdentificationRequest("Convidada", "fixture",
        codec.CreateConsent("local", submissions.Event.Id, 3, "Convidada", AdmittedAt)), null, caption, files);

    private Task<CreateSubmissionResponse> CreateAsync(CreateSubmissionRequest request, DateTimeOffset? ingress = null) =>
        Submissions.CreateAsync(SubmissionTestRepository.Code, attemptKey, secret, request, ingress ?? AdmittedAt,
            ingress ?? AdmittedAt, Guid.NewGuid(), Token);

    private Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(int? partNumber) =>
        Submissions.AuthorizeUploadAsync(SubmissionTestRepository.Code, attemptKey, fileKey, secret, partNumber, Token);

    private Task<SubmissionStatusResponse> SignalAsync() =>
        Submissions.SignalCompletionAsync(SubmissionTestRepository.Code, attemptKey, secret, Token);

    private void SetAttempt(CreatePhotoSubmissionCommand admitted) =>
        submissions.Attempt = new SubmissionAttemptEntity
        {
            RecordId = RecordId,
            Category = admitted.Category,
            Format = admitted.Format,
            State = "receiving",
            CompletionDeadlineAt = submissions.Event.CompletionDeadlineAt,
            HashKeyId = admitted.HashKeyId,
            PayloadHash = admitted.PayloadHash,
            AuthorizationHash = admitted.AuthorizationHash,
            AuthorizationExpiresAt = admitted.AuthorizationExpiresAt
        };

    private async Task SetMultipartAttemptAsync()
    {
        await CreateAsync(Request([File(size: MultipartSize)]));
        SetAttempt(Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted));
        photos.Submission = Video(MultipartSize, PartChecksums[..3], UploadId);
    }

    private PhotoSubmissionEntity Video(int size = 4, byte[][]? partChecksums = null, string? uploadId = null,
        PhotoReceiptModel? received = null, string category = "moment") => new()
        {
            SessionId = 7,
            EventId = submissions.Event.Id,
            RecordId = RecordId,
            FileId = 17,
            Position = 1,
            TemporaryObjectId = 11,
            OriginalObjectId = 12,
            State = received is null ? "receiving" : "validating",
            Format = "video",
            Category = category,
            Bucket = Bucket,
            TemporaryKey = Temporary,
            OriginalKey = Original,
            TemporaryVersion = received?.VersionId,
            ReceiptAt = received?.ReceivedAt,
            FileKey = fileKey,
            DeclaredMediaType = "video/mp4",
            DeclaredSize = size,
            Sha256 = Sha256,
            MultipartId = uploadId,
            PartSha256 = partChecksums,
            StartedAt = new DateTime(2032, 3, 1, 11, 59, 59),
            CompletionDeadlineAt = submissions.Event.CompletionDeadlineAt
        };

    private static VideoPartModel Part(int number, int size, byte[]? checksum = null) =>
        new(number, size, checksum ?? PartChecksums[number - 1], $"etag-{number}");

    private static PhotoReceiptModel Receipt(string version, DateTimeOffset time, string kind = "complete_multipart",
        int size = MultipartSize) => new(Bucket, Temporary, version, size, time, kind);

    private async Task AssertInvalidAsync(CreateSubmissionRequest request, string field)
    {
        var exception = await Assert.ThrowsAsync<EventRequestValidationException>(() => CreateAsync(request));

        Assert.Equal(field, exception.Field);
        Assert.Null(photos.Admitted);
        Assert.False(submissions.Transaction.Committed);
        Assert.Empty(submissions.OutboxMessages);
    }

    private static void AssertGrant(
        (string Bucket, string Key, string UploadId, int Number, int Size, byte[] Sha256, DateTimeOffset ExpiresAt) grant,
        int number, int size)
    {
        Assert.Equal((Bucket, Temporary, UploadId, number, size, Deadline),
            (grant.Bucket, grant.Key, grant.UploadId, grant.Number, grant.Size, grant.ExpiresAt));
        Assert.Equal(PartChecksums[number - 1], grant.Sha256);
    }
}
