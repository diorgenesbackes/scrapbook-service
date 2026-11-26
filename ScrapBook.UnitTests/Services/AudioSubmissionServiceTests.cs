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

public sealed class AudioSubmissionServiceTests
{
    private const int MaximumSize = 50_000_000;
    private const long RecordId = 19;
    private const string Bucket = "lab-audio";
    private const string Temporary = "submissions/temporary/audio";
    private const string Original = "submissions/original/audio";
    private const string Version = "received-version";
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly byte[] Sha256 = SHA256.HashData(Bytes);
    private static readonly byte[] BadSha256 = SHA256.HashData([4, 3, 2, 1]);
    private static readonly DateTimeOffset AdmittedAt = DateTimeOffset.Parse("2032-03-01T14:59:59Z");
    private static readonly DateTimeOffset Deadline = DateTimeOffset.Parse("2032-03-01T16:00:00Z");
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly Guid attemptKey = Guid.NewGuid();
    private readonly Guid fileKey = Guid.NewGuid();
    private readonly Guid correlationId = Guid.NewGuid();
    private readonly byte[] secret = new byte[32];
    private readonly SubmissionProtectionCodec codec = new("local", new Dictionary<string, byte[]> { ["local"] = new byte[32] });
    private readonly SubmissionTestRepository submissions = new();
    private readonly EventTestTimeProvider clock = new() { Now = AdmittedAt };
    private readonly PhotoSubmissionStorageStub storage = new(Bytes);
    private readonly PhotoSubmissionDecoderStub photoDecoder = new();
    private readonly AudioSubmissionDecoderStub audioDecoder = new(new("audio/wav", "wav", "pcm_s16le", 30m, Sha256));
    private readonly PhotoSubmissionRepositoryStub photos;

    public AudioSubmissionServiceTests() => photos = new(submissions.Event);

    private PhotoSubmissionService Media(bool audioEnabled = true) => new(photos, submissions, storage, photoDecoder,
        new(true, Bucket, AudioEnabled: audioEnabled), clock, audio: new(audioDecoder, storage));

    private SubmissionService Submissions => new(submissions, codec, clock, Media());

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("audio/mp4")]
    [InlineData("audio/wav")]
    [InlineData("audio/ogg")]
    [InlineData("audio/webm")]
    public async Task AdmitsOneAudioTestimonialWithCanonicalMediaTypeAtTheSizeAndCaptionLimitsAsync(string mediaType)
    {
        var caption = string.Concat(Enumerable.Repeat("🎵", 500));
        var result = await CreateAsync(Request([File(mediaType, MaximumSize)], caption: caption));

        Assert.True(result.Created);
        Assert.Equal("receiving", result.Status.State);
        Assert.Equal(Deadline, result.Status.CompletionDeadlineAt);
        Assert.Null(result.Status.ConfirmedAt);
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        Assert.Equal(("audio", "testimonial", "Convidada", caption, correlationId),
            (admitted.Format, admitted.Category, admitted.GuestName, admitted.Caption, admitted.CorrelationId));
        var intent = Assert.Single(admitted.Files);
        Assert.Equal(1, intent.Position);
        Assert.StartsWith("submissions/temporary/", intent.TemporaryKey);
        Assert.StartsWith("submissions/original/", intent.OriginalKey);
        Assert.Equal((fileKey, mediaType, MaximumSize),
            (intent.Manifest.FileKey, intent.Manifest.DeclaredMediaType, intent.Manifest.SizeBytes));
        Assert.Equal(Sha256, intent.Manifest.Sha256);
        Assert.Null(intent.Manifest.PartSha256);
        Assert.True(submissions.Transaction.Committed);
        Assert.Empty(submissions.OutboxMessages);
        Assert.Empty(storage.Authorizations);
    }

    [Theory]
    [InlineData("audio/x-wav", MaximumSize, "files[0].declaredMediaType")]
    [InlineData("video/webm", MaximumSize, "files[0].declaredMediaType")]
    [InlineData("audio/wav", 0, "files[0].sizeBytes")]
    [InlineData("audio/wav", MaximumSize + 1, "files[0].sizeBytes")]
    public async Task RejectsUnsupportedDeclarationsAndSizesAsync(string mediaType, int size, string field) =>
        await AssertInvalidAsync(Request([File(mediaType, size)]), field);

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-checksum")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB=")]
    public async Task RejectsAnAbsentMalformedOrNoncanonicalChecksumAsync(string? checksum) =>
        await AssertInvalidAsync(Request([File() with { Sha256 = checksum }]), "files[0].sha256");

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"]")]
    public async Task RejectsAnyMultipartManifestForAudioAsync(string parts)
    {
        var file = File(size: MaximumSize) with { PartSha256 = JsonSerializer.Deserialize<JsonElement>(parts) };

        await AssertInvalidAsync(Request([file]), "files[0].partSha256");
    }

    [Fact]
    public async Task RejectsAudioForMomentsOrWithTextAsync()
    {
        await AssertInvalidAsync(Request([File()], category: "moment"), "format");
        await AssertInvalidAsync(Request([File()]) with { Text = "Som" }, "text");
    }

    [Fact]
    public async Task RejectsAMissingOrMalformedSingleFileManifestAsync()
    {
        await AssertInvalidAsync(Request([]), "files");
        await AssertInvalidAsync(Request([File(), File() with { Position = 2, FileKey = Guid.NewGuid() }]), "files");
        await AssertInvalidAsync(Request([null]), "files");
        await AssertInvalidAsync(Request([File() with { Position = 2 }]), "files");
        await AssertInvalidAsync(Request([File() with { FileKey = Guid.Empty }]), "files");
    }

    [Fact]
    public async Task RejectsACaptionAbove500UnicodeScalarsAsync() =>
        await AssertInvalidAsync(Request([File()], caption: string.Concat(Enumerable.Repeat("🎵", 501))), "caption");

    [Fact]
    public async Task RejectsDisabledAudioAndStartsNoNewAttemptAtNoonAsync()
    {
        var disabled = new SubmissionService(submissions, codec, clock, Media(audioEnabled: false));
        await Assert.ThrowsAsync<AudioSubmissionDisabledException>(() => disabled.CreateAsync(SubmissionTestRepository.Code,
            attemptKey, secret, Request([File()]), AdmittedAt, AdmittedAt, correlationId, Token));

        clock.Now = AdmittedAt.AddSeconds(1);
        var closed = await Assert.ThrowsAsync<SubmissionWindowException>(() => CreateAsync(Request([File()])));
        Assert.Equal("closed", closed.WindowState);

        Assert.Null(photos.Admitted);
        Assert.False(submissions.Transaction.Committed);
    }

    [Fact]
    public async Task AuthorizesOnlyOneManifestBoundPutAndResumesTheSameAttemptAfterNoonAsync()
    {
        var request = Request([File(size: MaximumSize)]);
        await CreateAsync(request);
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        SetAttempt(admitted);
        photos.Submission = Audio(size: MaximumSize);
        clock.Now = Deadline.AddMinutes(-1);

        var replay = await CreateAsync(request);
        Assert.False(replay.Created);
        Assert.Equal("receiving", replay.Status.State);
        Assert.Same(admitted, photos.Admitted);
        await Assert.ThrowsAsync<SubmissionKeyReusedException>(
            () => CreateAsync(request with { Caption = "Uma mudança" }));
        await Assert.ThrowsAsync<SubmissionAttemptNotFoundException>(() => AuthorizeAsync(null, BadSha256));
        var partError = await Assert.ThrowsAsync<EventRequestValidationException>(() => AuthorizeAsync(1));
        Assert.Equal("partNumber", partError.Field);

        var status = await UploadStatusAsync();
        Assert.Equal(("put", null, Deadline), (status.Mode, status.PartSizeBytes, status.CompletionDeadlineAt));
        Assert.Empty(status.UploadedParts);
        var authorization = await AuthorizeAsync(null);
        Assert.Equal(("PUT", Deadline), (authorization.Method, authorization.ExpiresAt));
        var grant = Assert.Single(storage.Authorizations);
        Assert.Equal((Bucket, Temporary, "audio/wav", MaximumSize), (grant.Bucket, grant.Key, grant.MediaType, grant.Size));
        Assert.Equal(Sha256, grant.Hash);

        photos.Submission = Audio(receipt: Receipt(Deadline.AddSeconds(-1)), size: MaximumSize);
        Assert.Equal([1], (await UploadStatusAsync()).UploadedParts);
        await Assert.ThrowsAsync<AudioUploadUnavailableException>(() => AuthorizeAsync(null));
        clock.Now = Deadline;
        await Assert.ThrowsAsync<SubmissionDeadlineExceededException>(() => AuthorizeAsync(null));
        Assert.Single(storage.Authorizations);
    }

    [Fact]
    public async Task PinsTheExactPutVersionAtTheInclusiveDeadlineAndReturnsReceiptOnlyFromDurableConfirmationAsync()
    {
        await CreateAsync(Request([File()]));
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        SetAttempt(admitted);
        photos.Submission = Audio();
        clock.Now = Deadline.AddMinutes(1);

        var pending = await SignalAsync();
        Assert.Equal("reconciling", pending.State);
        Assert.Null(pending.ConfirmedAt);
        await Media().RecordReceiptAsync(Receipt(Deadline) with { EventKind = "complete_multipart", VersionId = "multipart" }, Token);
        Assert.Empty(photos.Pinned);
        var receipt = Receipt(Deadline);
        await Media().RecordReceiptAsync(receipt, Token);
        Assert.Equal(receipt, Assert.Single(photos.Pinned));
        Assert.True(Assert.Single(photos.Seals));

        photos.Submission = Audio(receipt);
        SetAttempt(admitted, "validating");
        await Media().RecordReceiptAsync(Receipt(Deadline) with { VersionId = "newer-version" }, Token);
        Assert.Equal(receipt, Assert.Single(photos.Pinned));
        Assert.Null((await SignalAsync()).ConfirmedAt);

        photos.ConfirmationAccepted = false;
        Assert.True(await Media().ProcessNextAsync(Token));
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
        Assert.Null((await SignalAsync()).ConfirmedAt);

        photos.ConfirmationAccepted = true;
        Assert.True(await Media().ProcessNextAsync(Token));

        var confirmation = Assert.Single(photos.Confirmations);
        var confirmedFile = Assert.Single(confirmation.Files);
        Assert.Equal(submissions.Event.CompletionDeadlineAt, confirmation.ReceivedAt);
        Assert.Equal((Version, "original-version"), (confirmedFile.File.TemporaryVersion, confirmedFile.Original.VersionId));
        Assert.Equal(audioDecoder.Metadata, confirmedFile.Audio);
        Assert.Null(confirmedFile.Image);
        Assert.Null(confirmedFile.Video);
        Assert.Equal((Temporary, Version, Original), Assert.Single(storage.Copies));
        var temporaryRead = (Bucket, Temporary, Version, (long)Bytes.Length);
        var originalRead = (Bucket, Original, "original-version", (long)Bytes.Length);
        Assert.Equal([temporaryRead, temporaryRead], audioDecoder.Reads);
        Assert.Equal([originalRead, originalRead], storage.OriginalReads);
        Assert.Equal(0, photoDecoder.Calls);
        Assert.True(submissions.Transaction.Committed);
        Assert.Collection(submissions.OutboxMessages,
            message => Assert.Equal(("event.first-record-confirmed.v1", correlationId, clock.Now),
                (message.Type, message.CorrelationId, message.OccurredAt)),
            message => Assert.Equal(("record.media-confirmed.v1", correlationId, clock.Now),
                (message.Type, message.CorrelationId, message.OccurredAt)));

        SetAttempt(admitted, "confirmed", confirmation.ConfirmedAt);
        var confirmed = await SignalAsync();
        Assert.Equal(confirmed, await Submissions.GetAsync(SubmissionTestRepository.Code, attemptKey, secret, Token));
        Assert.Equal(("confirmed", clock.Now), (confirmed.State, confirmed.ConfirmedAt));
        Assert.Equal(2, submissions.OutboxMessages.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(300_000_000, true)]
    [InlineData(300_000_001, false)]
    public async Task ValidatesThePositiveAndInclusiveDurationRangeAsync(int microseconds, bool valid)
    {
        photos.Submission = Audio(Receipt(Deadline));
        audioDecoder.Metadata = audioDecoder.Metadata with { Duration = microseconds / 1_000_000m };

        Assert.True(await Media().ProcessNextAsync(Token));

        if (valid)
        {
            var confirmation = Assert.Single(photos.Confirmations);
            Assert.Equal(audioDecoder.Metadata, Assert.Single(confirmation.Files).Audio);
        }
        else
        {
            AssertTerminalFailure("rejected", "audio-invalid");
        }
    }

    [Theory]
    [InlineData("audio/webm", "opus", true)]
    [InlineData("audio/wav", "", true)]
    [InlineData("audio/wav", "pcm_s16le", false)]
    public async Task RejectsMismatchedMediaTypeMissingCodecAndWrongChecksumAsync(string mediaType, string codecName, bool rightHash)
    {
        photos.Submission = Audio(Receipt(Deadline));
        audioDecoder.Metadata = audioDecoder.Metadata with
        {
            MediaType = mediaType, AudioCodec = codecName, Sha256 = rightHash ? Sha256 : BadSha256
        };

        Assert.True(await Media().ProcessNextAsync(Token));

        AssertTerminalFailure("rejected", "audio-invalid");
    }

    [Fact]
    public async Task RecordsOneTerminalResultForAnAttemptOverTheDecoderBudgetAsync()
    {
        await CreateAsync(Request([File()]));
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        SetAttempt(admitted, "validating");
        photos.Submission = Audio(Receipt(Deadline));
        audioDecoder.Error = new PhotoDecoderBudgetException();

        Assert.True(await Media().ProcessNextAsync(Token));

        AssertTerminalFailure("rejected", "audio-resource-budget");
        SetAttempt(admitted, "rejected", resultCode: "audio-resource-budget");
        photos.Submission = null;
        for (var retry = 0; retry < 3; retry++)
        {
            var status = await SignalAsync();
            Assert.Equal(("rejected", "audio-resource-budget", null), (status.State, status.Code, status.ConfirmedAt));
        }
        Assert.False(await Media().ProcessNextAsync(Token));
        Assert.Single(audioDecoder.Reads);
    }

    [Fact]
    public async Task LeavesTheSameAttemptPendingWhenItsPreservedOriginalHasAnIncorrectChecksumAsync()
    {
        photos.Submission = Audio(Receipt(Deadline));
        storage.OriginalSha256 = BadSha256;

        Assert.True(await Media().ProcessNextAsync(Token));

        Assert.Equal((clock.Now.AddSeconds(30), "audio-storage-unavailable"), photos.Scheduled);
        Assert.Null(photos.Terminated);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
        Assert.False(submissions.Transaction.Committed);
        Assert.Equal((Bucket, Original, "original-version", (long)Bytes.Length), Assert.Single(storage.OriginalReads));
    }

    [Fact]
    public async Task ReconcilesAStoredVersionUntilItReceivesLateProofAsync()
    {
        photos.Submission = Audio();
        clock.Now = Deadline.AddHours(25);
        storage.UnprovenVersion = Version;

        Assert.True(await Media().ProcessNextAsync(Token));
        Assert.Equal((clock.Now.AddHours(1), "audio-receipt-missing"), photos.Scheduled);
        Assert.Null(photos.Terminated);

        await Media().RecordReceiptAsync(Receipt(Deadline.AddTicks(1)), Token);
        Assert.True(await Media().ProcessNextAsync(Token));
        Assert.Empty(photos.Pinned);
        AssertTerminalFailure("rejected", "audio-received-late");
        Assert.Empty(audioDecoder.Reads);
    }

    [Fact]
    public async Task ExpiresAnAttemptWithoutAReceivedObjectAsync()
    {
        photos.Submission = Audio();
        clock.Now = Deadline.AddHours(25);

        Assert.True(await Media().ProcessNextAsync(Token));

        AssertTerminalFailure("expired", "submission-deadline-exceeded");
        Assert.Empty(audioDecoder.Reads);
    }

    private PhotoSubmissionFileRequest File(string mediaType = "audio/wav", int size = 4) =>
        new(fileKey, 1, mediaType, size, Convert.ToBase64String(Sha256));

    private CreateSubmissionRequest Request(IReadOnlyList<PhotoSubmissionFileRequest?> files, string category = "testimonial",
        string? caption = null) => new(category, "audio", new SubmissionIdentificationRequest("Convidada", "fixture",
        codec.CreateConsent("local", submissions.Event.Id, 3, "Convidada", AdmittedAt)), null, caption, files);

    private Task<CreateSubmissionResponse> CreateAsync(CreateSubmissionRequest request) =>
        Submissions.CreateAsync(SubmissionTestRepository.Code, attemptKey, secret, request, clock.Now, clock.Now, correlationId, Token);

    private Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(int? partNumber, byte[]? attemptSecret = null) =>
        Submissions.AuthorizeUploadAsync(SubmissionTestRepository.Code, attemptKey, fileKey, attemptSecret ?? secret, partNumber, Token);

    private Task<MediaUploadStatusResponse> UploadStatusAsync() =>
        Submissions.GetUploadStatusAsync(SubmissionTestRepository.Code, attemptKey, fileKey, secret, Token);

    private Task<SubmissionStatusResponse> SignalAsync() =>
        Submissions.SignalCompletionAsync(SubmissionTestRepository.Code, attemptKey, secret, Token);

    private void SetAttempt(CreatePhotoSubmissionCommand admitted, string state = "receiving", DateTime? confirmedAt = null,
        string? resultCode = null) => submissions.Attempt = new SubmissionAttemptEntity
    {
        RecordId = RecordId,
        Category = admitted.Category,
        Format = admitted.Format,
        State = state,
        CompletionDeadlineAt = submissions.Event.CompletionDeadlineAt,
        ConfirmedAt = confirmedAt,
        ResultCode = resultCode,
        HashKeyId = admitted.HashKeyId,
        PayloadHash = admitted.PayloadHash,
        AuthorizationHash = admitted.AuthorizationHash,
        AuthorizationExpiresAt = admitted.AuthorizationExpiresAt
    };

    private PhotoSubmissionEntity Audio(PhotoReceiptModel? receipt = null, int size = 4) => new()
    {
        SessionId = 7,
        EventId = submissions.Event.Id,
        RecordId = RecordId,
        FileId = 17,
        Position = 1,
        TemporaryObjectId = 11,
        OriginalObjectId = 12,
        State = receipt is null ? "receiving" : "validating",
        Format = "audio",
        Bucket = Bucket,
        TemporaryKey = Temporary,
        OriginalKey = Original,
        TemporaryVersion = receipt?.VersionId,
        ReceiptAt = receipt?.ReceivedAt,
        FileKey = fileKey,
        DeclaredMediaType = "audio/wav",
        DeclaredSize = size,
        Sha256 = Sha256,
        StartedAt = new DateTime(2032, 3, 1, 11, 59, 59),
        CompletionDeadlineAt = submissions.Event.CompletionDeadlineAt,
        CorrelationId = correlationId,
    };

    private static PhotoReceiptModel Receipt(DateTimeOffset at) => new(Bucket, Temporary, Version, Bytes.Length, at);

    private async Task AssertInvalidAsync(CreateSubmissionRequest request, string field)
    {
        var exception = await Assert.ThrowsAsync<EventRequestValidationException>(() => CreateAsync(request));

        Assert.Equal(field, exception.Field);
        Assert.Null(photos.Admitted);
        Assert.False(submissions.Transaction.Committed);
        Assert.Empty(submissions.OutboxMessages);
    }

    private void AssertTerminalFailure(string state, string code)
    {
        Assert.Equal((state, code), photos.Terminated);
        Assert.Equal(code, Assert.Single(photos.Problems!).Value);
        Assert.Empty(storage.Copies);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
    }
}
