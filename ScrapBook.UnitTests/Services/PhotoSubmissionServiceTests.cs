using System.Security.Cryptography;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;
using ScrapBook.Infrastructure.Codecs;

namespace ScrapBook.UnitTests.Services;

public sealed class PhotoSubmissionServiceTests
{
    private const string Bucket = "lab-photos";
    private const string Temporary = "submissions/temporary/fixture";
    private const string Original = "submissions/original/fixture";
    private const string Version = "received-version";
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly byte[] Hash = SHA256.HashData(Bytes);
    private static readonly DateTimeOffset Closing = DateTimeOffset.Parse("2032-03-01T15:00:00Z");
    private static readonly DateTimeOffset Admitted = Closing.AddSeconds(-1);
    private static readonly DateTimeOffset Deadline = Closing.AddHours(1);
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly SubmissionTestRepository submissions = new();
    private readonly EventTestTimeProvider clock = new() { Now = Admitted };
    private readonly PhotoSubmissionStorageStub storage = new(Bytes);
    private readonly PhotoSubmissionDecoderStub decoder = new();
    private readonly SubmissionProtectionCodec codec = new("local", new Dictionary<string, byte[]> { ["local"] = new byte[32] });
    private readonly Guid fileKey = Guid.NewGuid();
    private readonly PhotoSubmissionRepositoryStub photos;

    public PhotoSubmissionServiceTests()
    {
        photos = new(submissions.Event);
    }

    private PhotoSubmissionService Photos => new(photos, submissions, storage, decoder, new(true, Bucket), clock);

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/heic")]
    [InlineData("image/heif")]
    public async Task AdmitsOneAllowedPhotoAtInclusiveSizeAndCaptionLimitsAsync(string mediaType)
    {
        var caption = string.Concat(Enumerable.Repeat("🌅", 500));
        var result = await CreateAsync(Request([File(mediaType, 20_000_000)], caption));

        Assert.True(result.Created);
        Assert.Equal("receiving", result.Status.State);
        Assert.Equal(Deadline, result.Status.CompletionDeadlineAt);
        var admitted = Assert.IsType<CreatePhotoSubmissionCommand>(photos.Admitted);
        Assert.Equal(caption, admitted.Caption);
        Assert.Equal(fileKey, admitted.File.FileKey);
        Assert.Equal(mediaType, admitted.File.DeclaredMediaType);
        Assert.Equal(20_000_000, admitted.File.SizeBytes);
        Assert.Equal(Hash, admitted.File.Sha256);
        Assert.Equal(Bucket, admitted.Bucket);
        Assert.StartsWith("submissions/temporary/", admitted.TemporaryKey);
        Assert.StartsWith("submissions/original/", admitted.OriginalKey);
        Assert.True(submissions.Transaction.Committed);
        Assert.Empty(storage.Authorizations);
        Assert.Null(submissions.Outbox);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public async Task RejectsAnyManifestOtherThanOnePhotoAtPositionOneAsync(int count, int position)
    {
        var files = Enumerable.Range(0, count).Select(_ => File() with { Position = position }).ToArray();

        await AssertInvalidAsync(Request(files), "files");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20_000_001)]
    public async Task RejectsPhotoSizeOutsideTheInclusiveRangeAsync(int size) =>
        await AssertInvalidAsync(Request([File(size: size)]), "files[0].sizeBytes");

    [Theory]
    [InlineData("image/webp")]
    [InlineData("image/avif")]
    [InlineData("video/mp4")]
    public async Task RejectsPhotoMediaTypeOutsideTheAllowListAsync(string mediaType) =>
        await AssertInvalidAsync(Request([File(mediaType)]), "files[0].declaredMediaType");

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task RejectsChecksumWithoutCanonicalBase64EncodingOf32BytesAsync(string checksum) =>
        await AssertInvalidAsync(Request([File() with { Sha256 = checksum }]), "files[0].sha256");

    [Fact]
    public async Task RejectsCaptionAbove500UnicodeScalarsAsync()
    {
        var caption = string.Concat(Enumerable.Repeat("🌅", 501));
        await AssertInvalidAsync(Request([File()], caption), "caption");
    }

    [Fact]
    public async Task RejectsNewPhotoAtNoonAsync()
    {
        var service = new SubmissionService(submissions, codec, clock, Photos);
        await Assert.ThrowsAsync<SubmissionWindowException>(() => service.CreateAsync(SubmissionTestRepository.Code,
            Guid.NewGuid(), new byte[32], Request([File()]), Closing, Closing, Guid.NewGuid(), Token));
        Assert.Null(photos.Admitted);
    }

    [Fact]
    public async Task RenewsAuthorizationForTheSameTemporaryObjectAndStopsAtDeadlineAsync()
    {
        photos.Submission = Submission();
        var credentialExpiry = Deadline.AddDays(1);
        var first = await Photos.AuthorizeAsync(19, fileKey, credentialExpiry, Token);
        Assert.Equal(Admitted.AddMinutes(10), first.ExpiresAt);

        clock.Now = Deadline.AddMinutes(-2);
        var renewal = await Photos.AuthorizeAsync(19, fileKey, credentialExpiry, Token);
        Assert.Equal(Deadline, renewal.ExpiresAt);
        Assert.All(storage.Authorizations, grant =>
        {
            Assert.Equal((Bucket, Temporary, "image/png", Bytes.Length), (grant.Bucket, grant.Key, grant.MediaType, grant.Size));
            Assert.Equal(Hash, grant.Hash);
        });
        Assert.Equal(2, storage.Authorizations.Count);

        await Assert.ThrowsAsync<SubmissionAttemptNotFoundException>(() =>
            Photos.AuthorizeAsync(19, Guid.NewGuid(), credentialExpiry, Token));
        clock.Now = Deadline;
        await Assert.ThrowsAsync<SubmissionDeadlineExceededException>(() =>
            Photos.AuthorizeAsync(19, fileKey, credentialExpiry, Token));
        Assert.Equal(2, storage.Authorizations.Count);
    }

    [Fact]
    public async Task PinsTheExactDeadlineFromLateEvidenceAndKeepsItOnDuplicateOrLaterPutAsync()
    {
        clock.Now = Deadline.AddHours(2);
        photos.Submission = Submission();
        var receipt = Receipt(Version, Deadline);

        await Photos.RecordReceiptAsync(receipt, Token);
        Assert.Equal(receipt, Assert.Single(photos.Pinned));
        Assert.Contains(Version, photos.ReceivedVersions);
        Assert.True(submissions.Transaction.Committed);

        photos.Submission = Submission(receipt);
        await Photos.RecordReceiptAsync(receipt, Token);
        await Photos.RecordReceiptAsync(Receipt("later-put", Deadline.AddTicks(1)), Token);
        Assert.Equal(receipt, Assert.Single(photos.Pinned));
        Assert.Equal(2, photos.ReceivedVersions.Count);
    }

    [Fact]
    public async Task RecordsLateEvidenceForReconciliationWithoutPinningItAsync()
    {
        clock.Now = Deadline.AddHours(2);
        photos.Submission = Submission();

        await Photos.RecordReceiptAsync(Receipt("late-version", Deadline.AddTicks(1)), Token);

        Assert.Contains("late-version", photos.ReceivedVersions);
        Assert.Empty(photos.Pinned);
        Assert.Empty(photos.Confirmations);
    }

    [Fact]
    public async Task KeepsUnknownVersionPendingUntilReceiptIsReconciledAsync()
    {
        clock.Now = Deadline.AddHours(24);
        photos.Submission = Submission();
        storage.UnprovenVersion = "unknown-version";

        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal((clock.Now.AddHours(1), "photo-receipt-missing"), photos.Scheduled);
        Assert.Null(photos.Terminated);
        Assert.Empty(storage.Reads);

        await Photos.RecordReceiptAsync(Receipt("unknown-version", Deadline.AddTicks(1)), Token);
        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal(("expired", "submission-deadline-exceeded"), photos.Terminated);
        Assert.Empty(photos.Confirmations);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("checksum")]
    [InlineData("decode")]
    [InlineData("format")]
    public async Task RejectsInvalidPinnedBytesBeforeCopyOrConfirmationAsync(string failure)
    {
        photos.Submission = Submission(Receipt(Version, Deadline));
        switch (failure)
        {
            case "length":
                storage.Bytes = [1, 2, 3];
                break;
            case "checksum":
                storage.Bytes = [4, 3, 2, 1];
                break;
            case "decode":
                decoder.Error = new PhotoContentInvalidException();
                break;
            case "format":
                decoder.Image = new("image/jpeg", "jpeg", 8, 6, null);
                break;
        }

        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal((Temporary, Version), Assert.Single(storage.Reads));
        Assert.Equal(failure is "decode" or "format" ? 1 : 0, decoder.Calls);
        Assert.Equal(("rejected", "photo-invalid"), photos.Terminated);
        Assert.Empty(storage.Copies);
        Assert.Empty(photos.Confirmations);
        Assert.Null(submissions.Outbox);
        Assert.False(submissions.Transaction.Committed);
    }

    [Fact]
    public async Task ReusesThePreservedVersionWhenTheFirstConfirmationCannotCommitAsync()
    {
        clock.Now = Deadline.AddMinutes(3);
        photos.Submission = Submission(Receipt(Version, Deadline));
        photos.ConfirmationAccepted = false;

        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal((Temporary, Version, Original), Assert.Single(storage.Copies));
        Assert.Empty(photos.Confirmations);
        Assert.Null(submissions.Outbox);
        Assert.False(submissions.Transaction.Committed);

        photos.ConfirmationAccepted = true;
        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Single(storage.Copies);
        var originalRead = (Original, "original-version");
        var sourceRead = (Temporary, Version);
        Assert.Equal([sourceRead, originalRead, sourceRead, originalRead], storage.Reads);
        var confirmation = Assert.Single(photos.Confirmations);
        Assert.Equal(Version, confirmation.Original.SourceVersion);
        Assert.Equal("original-version", confirmation.Original.VersionId);
        Assert.Equal(new DateTime(2032, 3, 1, 13, 0, 0), confirmation.ReceivedAt);
        Assert.Equal(clock.Now, confirmation.CommittedAt);
        Assert.Equal(
            ["event.first-record-confirmed.v1", "record.media-confirmed.v1"],
            submissions.OutboxMessages.Select(message => message.Type));
        Assert.True(submissions.Transaction.Committed);
    }

    [Fact]
    public async Task ReschedulesAnUnavailableDecoderWithoutRejectingThePhotoAsync()
    {
        clock.Now = Deadline.AddMinutes(3);
        photos.Submission = Submission(Receipt(Version, Deadline));
        decoder.Error = new PhotoDecoderUnavailableException();

        Assert.True(await Photos.ProcessNextAsync(Token));

        Assert.Equal((clock.Now.AddSeconds(30), "photo-decoder-unavailable"), photos.Scheduled);
        Assert.Null(photos.Terminated);
        Assert.Empty(storage.Copies);
        Assert.Empty(photos.Confirmations);
    }

    [Fact]
    public async Task MarksOnlyTheSelectedTemporaryVersionAfterDeletionSucceedsAsync()
    {
        clock.Now = Deadline.AddHours(24);
        var cleanup = new PhotoCleanupModel(11, Bucket, Temporary, "late-version");
        photos.Cleanup = cleanup;
        storage.DeleteUnavailable = true;

        await Assert.ThrowsAsync<PhotoStorageUnavailableException>(() => Photos.CleanNextAsync(Token));
        Assert.Null(photos.MarkedDeleted);

        storage.DeleteUnavailable = false;
        Assert.True(await Photos.CleanNextAsync(Token));

        Assert.Equal((clock.Now, Deadline), photos.CleanupWindow);
        Assert.Equal((Bucket, Temporary, cleanup.VersionId), Assert.Single(storage.Deletions));
        Assert.Equal(cleanup, photos.MarkedDeleted);
    }

    private PhotoSubmissionFileRequest File(string mediaType = "image/png", int size = 4) =>
        new(fileKey, 1, mediaType, size, Convert.ToBase64String(Hash));

    private CreateSubmissionRequest Request(IReadOnlyList<PhotoSubmissionFileRequest?> files, string? caption = null) =>
        new("testimonial", "photo", new SubmissionIdentificationRequest("Convidada", "fixture",
            codec.CreateConsent("local", 5, 3, "Convidada", Admitted)), null, caption, files);

    private Task<CreateSubmissionResponse> CreateAsync(CreateSubmissionRequest request) =>
        new SubmissionService(submissions, codec, clock, Photos).CreateAsync(SubmissionTestRepository.Code, Guid.NewGuid(),
            new byte[32], request, Admitted, Admitted, Guid.NewGuid(), Token);

    private async Task AssertInvalidAsync(CreateSubmissionRequest request, string field)
    {
        var exception = await Assert.ThrowsAsync<EventRequestValidationException>(() => CreateAsync(request));
        Assert.Equal(field, exception.Field);
        Assert.Null(photos.Admitted);
        Assert.False(submissions.Transaction.Committed);
    }

    private static PhotoReceiptModel Receipt(string version, DateTimeOffset time) =>
        new(Bucket, Temporary, version, Bytes.Length, time);

    private PhotoSubmissionEntity Submission(PhotoReceiptModel? receipt = null) => new()
    {
        SessionId = 7,
        EventId = submissions.Event.Id,
        RecordId = 19,
        FileId = 9,
        TemporaryObjectId = 11,
        OriginalObjectId = 12,
        State = receipt is null ? "receiving" : "validating",
        Bucket = Bucket,
        TemporaryKey = Temporary,
        OriginalKey = Original,
        TemporaryVersion = receipt?.VersionId,
        ReceiptAt = receipt?.ReceivedAt,
        FileKey = fileKey,
        DeclaredMediaType = "image/png",
        DeclaredSize = Bytes.Length,
        Sha256 = Hash,
        StartedAt = new DateTime(2032, 3, 1, 11, 59, 59),
        CompletionDeadlineAt = submissions.Event.CompletionDeadlineAt,
    };
}
