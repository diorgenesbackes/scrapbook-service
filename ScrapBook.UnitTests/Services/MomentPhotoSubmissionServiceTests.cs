using System.Globalization;
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

public sealed class MomentPhotoSubmissionServiceTests
{
    private const string Bucket = "lab-photos";
    private const long RecordId = 19;
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly byte[] Hash = SHA256.HashData(Bytes);
    private static readonly DateTimeOffset Deadline = DateTimeOffset.Parse("2032-03-01T16:00:00Z");
    private static readonly DateTimeOffset AdmittedAt = Deadline.AddHours(-1).AddSeconds(-1);
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly Guid attemptKey = Guid.NewGuid();
    private readonly byte[] secret = new byte[32];
    private readonly SubmissionTestRepository submissions = new();
    private readonly EventTestTimeProvider clock = new() { Now = AdmittedAt };
    private readonly PhotoSubmissionStorageStub storage = new(Bytes);
    private readonly PhotoSubmissionDecoderStub decoder = new();
    private readonly SubmissionProtectionCodec codec = new("local", new Dictionary<string, byte[]> { ["local"] = new byte[32] });
    private readonly PhotoSubmissionRepositoryStub photos;

    public MomentPhotoSubmissionServiceTests()
    {
        photos = new(submissions.Event);
    }

    private PhotoSubmissionService Photos => new(photos, submissions, storage, decoder, new(true, Bucket), clock);
    private SubmissionService Submissions => new(submissions, codec, clock, Photos);

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task AdmitsOrderedMomentAtInclusiveFileAndCaptionLimitsAsync(int count)
    {
        var files = Enumerable.Range(1, count).Select(position => File(position, position == 1 ? 20_000_000 : 1)).ToArray();
        var caption = string.Concat(Enumerable.Repeat("🌅", 500));

        var response = await CreateAsync(Request(files, caption));

        Assert.True(response.Created);
        Assert.Equal("receiving", response.Status.State);
        Assert.Equal(Deadline, response.Status.CompletionDeadlineAt);
        Assert.Equal(Enumerable.Range(1, count), response.Status.PendingPositions);
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        Assert.Equal("moment", admitted.Category);
        Assert.Equal(caption, admitted.Caption);
        Assert.Equal(files.Select(file => (file.Position, file.FileKey, file.SizeBytes)),
            admitted.Files.Select(file => (file.Position, file.Manifest.FileKey, file.Manifest.SizeBytes)));
        Assert.Equal(count, admitted.Files.Select(file => file.TemporaryKey).Distinct().Count());
        Assert.Equal(count, admitted.Files.Select(file => file.OriginalKey).Distinct().Count());
        Assert.True(submissions.Transaction.Committed);
        Assert.Empty(storage.Authorizations);
        Assert.Empty(submissions.OutboxMessages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task RejectsMomentWithFileCountOutsideOneThroughTenAsync(int count)
    {
        var files = Enumerable.Range(1, count).Select(position => File(position)).ToArray();

        await AssertInvalidAsync(Request(files), "files");
    }

    [Theory]
    [InlineData("repeated-key")]
    [InlineData("empty-key")]
    [InlineData("repeated-position")]
    [InlineData("reordered-positions")]
    [InlineData("position-gap")]
    [InlineData("zero-position")]
    public async Task RejectsMomentManifestWithoutDistinctKeysAndConsecutiveOrderedPositionsAsync(string failure)
    {
        var first = File(1);
        var second = File(2);
        IReadOnlyList<PhotoSubmissionFileRequest?> files = failure switch
        {
            "repeated-key" => [first, second with { FileKey = first.FileKey }],
            "empty-key" => [first, second with { FileKey = Guid.Empty }],
            "repeated-position" => [first, second with { Position = first.Position }],
            "reordered-positions" => [second, first],
            "position-gap" => [first, second with { Position = 3 }],
            "zero-position" => [first with { Position = 0 }, second],
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        await AssertInvalidAsync(Request(files), "files");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20_000_001)]
    public async Task RejectsAnOutOfRangeSizeOnTheSecondPhotoAsync(int size)
    {
        await AssertInvalidAsync(Request([File(1), File(2, size)]), "files[1].sizeBytes");
    }

    [Fact]
    public async Task RejectsMomentWithCaptionAbove500UnicodeScalarsAsync()
    {
        var caption = string.Concat(Enumerable.Repeat("🌅", 501));

        await AssertInvalidAsync(Request([File(1), File(2)], caption), "caption");
    }

    [Fact]
    public async Task ResumesTheAuthorizedAttemptWithOnlyItsUnreceivedPositionsAsync()
    {
        var request = Request([File(1), File(2), File(3)], "Legenda");
        await CreateAsync(request);
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        SetAttempt(admitted, "receiving", pending: [2]);
        SetFiles(Photo(1, Deadline.AddSeconds(-4)), Photo(2), Photo(3, Deadline.AddSeconds(-2)));
        clock.Now = Deadline.AddSeconds(-1);

        var status = await Submissions.GetAsync(SubmissionTestRepository.Code, attemptKey, secret, Token);
        var completion = await Submissions.SignalPhotoCompletionAsync(SubmissionTestRepository.Code, attemptKey, secret, Token);
        var replay = await Submissions.CreateAsync(SubmissionTestRepository.Code, attemptKey, secret, request, clock.Now,
            clock.Now, Guid.NewGuid(), Token);

        Assert.NotNull(status.PendingPositions);
        Assert.Equal([2], status.PendingPositions);
        Assert.Equal(status, completion);
        Assert.False(replay.Created);
        Assert.Equal(status, replay.Status);
        await Assert.ThrowsAsync<PhotoUploadUnavailableException>(() => AuthorizeAsync(File(1).FileKey));
        await Assert.ThrowsAsync<PhotoUploadUnavailableException>(() => AuthorizeAsync(File(3).FileKey));
        await Assert.ThrowsAsync<SubmissionAttemptNotFoundException>(() => AuthorizeAsync(Guid.NewGuid()));
        var grant = await AuthorizeAsync(File(2).FileKey);

        Assert.Equal(Photo(2).TemporaryKey, Assert.Single(storage.Authorizations).Key);
        Assert.Equal(Deadline, grant.ExpiresAt);

        clock.Now = Deadline;
        var reconciling = await Submissions.GetAsync(SubmissionTestRepository.Code, attemptKey, secret, Token);
        Assert.Equal(new SubmissionStatusResponse("reconciling"), reconciling);
        await Assert.ThrowsAsync<SubmissionDeadlineExceededException>(() => AuthorizeAsync(File(2).FileKey));

        var reordered = Request([File(2) with { Position = 1 }, File(1) with { Position = 2 }, File(3)], "Legenda");
        await Assert.ThrowsAsync<SubmissionKeyReusedException>(() => CreateAsync(reordered));
    }

    [Fact]
    public async Task SealsOnlyAfterEveryTrustedReceiptAndKeepsPinnedVersionsOnReplayAsync()
    {
        var first = Photo(1, Deadline.AddSeconds(-3));
        var second = Photo(2, Deadline.AddSeconds(-2));
        var third = Photo(3, Deadline);
        var latestReceipt = Receipt(third, third.TemporaryVersion!, third.ReceiptAt!.Value);
        var firstReceipt = Receipt(first, first.TemporaryVersion!, first.ReceiptAt!.Value);
        var secondReceipt = Receipt(second, second.TemporaryVersion!, second.ReceiptAt!.Value);
        clock.Now = Deadline.AddMinutes(5);
        SetFiles(Photo(1), Photo(2), Photo(3));

        await Photos.RecordReceiptAsync(latestReceipt, Token);
        SetFiles(Photo(1), Photo(2), third);
        await Photos.RecordReceiptAsync(firstReceipt, Token);
        SetFiles(first, Photo(2), third);
        await Photos.RecordReceiptAsync(secondReceipt, Token);

        Assert.Equal([latestReceipt, firstReceipt, secondReceipt], photos.Pinned);
        Assert.Equal([false, false, true], photos.Seals);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);

        SetFiles(Photo(1, first.ReceiptAt, state: "validating"), Photo(2, second.ReceiptAt, state: "validating"),
            Photo(3, third.ReceiptAt, state: "validating"));
        await Photos.RecordReceiptAsync(latestReceipt, Token);
        await Photos.RecordReceiptAsync(Receipt(third, "later-version", Deadline.AddTicks(1)), Token);

        Assert.Equal(3, photos.Pinned.Count);
        Assert.Equal(4, photos.ReceivedVersions.Count);
    }

    [Fact]
    public async Task UsesTheLargestReceiptAndPreservesEveryOriginalOnceAcrossConfirmationRetryAsync()
    {
        var files = new[]
        {
            Photo(1, Deadline.AddSeconds(-4), state: "validating"),
            Photo(2, Deadline, state: "validating"),
            Photo(3, Deadline.AddSeconds(-3), state: "validating"),
        };
        SetFiles(files);
        clock.Now = Deadline.AddMinutes(12);
        photos.ConfirmationAccepted = false;

        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal(files.Select(file => (file.TemporaryKey, file.TemporaryVersion!, file.OriginalKey)), storage.Copies);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(submissions.OutboxMessages);
        Assert.False(submissions.Transaction.Committed);

        photos.ConfirmationAccepted = true;
        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal(files.Length, storage.Copies.Count);
        var confirmation = Assert.Single(photos.Confirmations);
        Assert.Equal(new DateTime(2032, 3, 1, 13, 0, 0), confirmation.ReceivedAt);
        Assert.Equal(clock.Now, confirmation.CommittedAt);
        Assert.Equal(files.Select(file => file.FileId), confirmation.Files.Select(file => file.File.FileId));
        Assert.Equal(files.Select(file => file.TemporaryVersion), confirmation.Files.Select(file => file.Original.SourceVersion));
        Assert.Equal(new[] { new DateTime(2032, 3, 1, 12, 59, 56), new DateTime(2032, 3, 1, 13, 0, 0),
            new DateTime(2032, 3, 1, 12, 59, 57) }, confirmation.Files.Select(file => file.ReceivedAt));
        Assert.Single(submissions.OutboxMessages, message => message.Type == "event.first-record-confirmed.v1");
        var media = submissions.OutboxMessages.Where(message => message.Type == "record.media-confirmed.v1").ToArray();
        Assert.Equal(files.Select(file => file.FileId), media.Select(MediaFileId));
        Assert.Equal(files.Length, media.Select(message => message.MessageId).Distinct().Count());
        Assert.True(submissions.Transaction.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiresASetWithMissingOrLatePhotosAndReportsTheirPositionsAsync(bool arrivesLate)
    {
        var first = Photo(1, Deadline.AddSeconds(-2));
        var second = Photo(2, arrivesLate ? Deadline.AddSeconds(-1) : null);
        var final = Photo(3);
        SetFiles(first, second, final);
        clock.Now = Deadline.AddHours(24);

        if (arrivesLate)
        {
            var late = Receipt(final, "late-version", Deadline.AddTicks(1));
            storage.UnprovenVersion = late.VersionId;
            await Photos.RecordReceiptAsync(late, Token);
            Assert.Contains(late.VersionId, photos.ReceivedVersions);
            Assert.Empty(photos.Pinned);
        }

        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal(("expired", "submission-deadline-exceeded"), photos.Terminated);
        long[] affectedFileIds = arrivesLate ? [final.FileId] : [second.FileId, final.FileId];
        Assert.Equal(affectedFileIds, photos.Problems!.Keys);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(storage.Reads);
        Assert.Empty(storage.Copies);
        Assert.Empty(submissions.OutboxMessages);
        await AssertTerminalStatusAsync("expired", arrivesLate ? [3] : [2, 3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsTheWholeSetAndReportsEachInvalidPhotoAsync(bool thirdInvalid)
    {
        var first = Photo(1, Deadline.AddSeconds(-1), state: "validating");
        var invalid = Photo(2, Deadline, state: "validating", sha256: new byte[32]);
        var third = Photo(3, Deadline, state: "validating", sha256: thirdInvalid ? new byte[32] : null);
        SetFiles(first, invalid, third);
        clock.Now = Deadline.AddMinutes(1);

        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal(new[] { first.TemporaryKey, invalid.TemporaryKey, third.TemporaryKey }, storage.Reads.Select(read => read.Key));
        Assert.Equal(("rejected", "photo-invalid"), photos.Terminated);
        long[] affected = thirdInvalid ? [invalid.FileId, third.FileId] : [invalid.FileId];
        Assert.Equal(affected, photos.Problems!.Keys);
        Assert.Empty(photos.Confirmations);
        Assert.Empty(storage.Copies);
        Assert.Empty(submissions.OutboxMessages);
        Assert.False(submissions.Transaction.Committed);
        await AssertTerminalStatusAsync("rejected", thirdInvalid ? [2, 3] : [2]);
    }

    private void SetFiles(params PhotoSubmissionEntity[] files) => photos.Files = files;

    private PhotoSubmissionEntity Photo(int position, DateTimeOffset? receipt = null, string state = "receiving",
        byte[]? sha256 = null) => new()
        {
            SessionId = 7,
            EventId = submissions.Event.Id,
            RecordId = RecordId,
            FileId = 50 + position,
            TemporaryObjectId = 60 + position,
            OriginalObjectId = 70 + position,
            State = state,
            Position = position,
            Bucket = Bucket,
            TemporaryKey = $"submissions/temporary/photo-{position}",
            OriginalKey = $"submissions/original/photo-{position}",
            TemporaryVersion = receipt is null ? null : $"received-{position}",
            ReceiptAt = receipt,
            FileKey = File(position).FileKey,
            DeclaredMediaType = "image/png",
            DeclaredSize = Bytes.Length,
            Sha256 = sha256 ?? Hash,
            StartedAt = new DateTime(2032, 3, 1, 11, 59, 59),
            CompletionDeadlineAt = submissions.Event.CompletionDeadlineAt,
        };

    private static PhotoReceiptModel Receipt(PhotoSubmissionEntity photo, string version, DateTimeOffset receivedAt) =>
        new(photo.Bucket, photo.TemporaryKey, version, photo.DeclaredSize, receivedAt);

    private static long MediaFileId(SubmissionOutboxEntity message)
    {
        using var payload = JsonDocument.Parse(message.Payload);
        return long.Parse(payload.RootElement.GetProperty("fileId").GetString()!, CultureInfo.InvariantCulture);
    }

    private Task<PhotoUploadAuthorizationResponse> AuthorizeAsync(Guid fileKey) =>
        Submissions.AuthorizePhotoAsync(SubmissionTestRepository.Code, attemptKey, fileKey, secret, Token);

    private void SetAttempt(CreatePhotoSubmissionCommand admitted, string state, int[]? pending = null,
        int[]? problems = null, string? resultCode = null)
    {
        submissions.Attempt = new SubmissionAttemptEntity
        {
            RecordId = RecordId,
            Category = admitted.Category,
            Format = "photo",
            State = state,
            PendingPositions = pending,
            ProblemPositions = problems,
            ProblemCodes = problems?.Select(position => photos.Problems![photos.Files!.Single(file => file.Position == position).FileId]).ToArray(),
            CompletionDeadlineAt = submissions.Event.CompletionDeadlineAt,
            ResultCode = resultCode,
            HashKeyId = admitted.HashKeyId,
            PayloadHash = admitted.PayloadHash,
            AuthorizationHash = admitted.AuthorizationHash,
            AuthorizationExpiresAt = admitted.AuthorizationExpiresAt,
        };
    }

    private async Task AssertTerminalStatusAsync(string state, int[] positions)
    {
        await CreateAsync(Request(photos.Files!.Select(file => File(file.Position)).ToArray()));
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        var filePositions = photos.Files!.Where(file => photos.Problems!.ContainsKey(file.FileId))
            .Select(file => file.Position).ToArray();
        SetAttempt(admitted, state, problems: filePositions, resultCode: photos.Terminated!.Value.Code);

        var status = await Submissions.GetAsync(SubmissionTestRepository.Code, attemptKey, secret, Token);

        Assert.Equal(state, status.State);
        Assert.NotNull(status.Problems);
        Assert.Equal(positions, status.Problems.Select(problem => problem.Position));
        Assert.Null(status.PendingPositions);
        Assert.Null(status.ConfirmedAt);
    }

    private static PhotoSubmissionFileRequest File(int position, int size = 4) =>
        new(new Guid(11 - position, 0, 0, new byte[8]), position, "image/png", size, Convert.ToBase64String(Hash));

    private CreateSubmissionRequest Request(IReadOnlyList<PhotoSubmissionFileRequest?> files, string? caption = null) =>
        new("moment", "photo", new SubmissionIdentificationRequest("Convidada", "fixture",
            codec.CreateConsent("local", submissions.Event.Id, 3, "Convidada", AdmittedAt)), null, caption, files);

    private Task<CreateSubmissionResponse> CreateAsync(CreateSubmissionRequest request) =>
        Submissions.CreateAsync(SubmissionTestRepository.Code, attemptKey, secret, request, AdmittedAt, AdmittedAt,
            Guid.NewGuid(), Token);

    private async Task AssertInvalidAsync(CreateSubmissionRequest request, string field)
    {
        var exception = await Assert.ThrowsAsync<EventRequestValidationException>(() => CreateAsync(request));

        Assert.Equal(field, exception.Field);
        Assert.Null(photos.Admitted);
        Assert.False(submissions.Transaction.Committed);
        Assert.Empty(submissions.OutboxMessages);
    }
}
