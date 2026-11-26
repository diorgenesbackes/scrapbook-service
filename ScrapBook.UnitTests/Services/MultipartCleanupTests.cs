using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class MultipartCleanupTests
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.Parse("2032-03-01T16:00:00Z");
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly EventTestTimeProvider clock = new() { Now = Deadline.AddDays(2) };
    private readonly MultipartCleanupRepositoryStub jobs = new();
    private readonly MultipartCleanupStorageStub multipart = new();
    private readonly PhotoSubmissionService service;

    public MultipartCleanupTests()
    {
        var submissions = new SubmissionTestRepository();
        var photos = new PhotoSubmissionRepositoryStub(submissions.Event);
        var objects = new PhotoSubmissionStorageStub([]);
        var video = new VideoSubmissionService(photos, jobs, multipart, multipart, clock);
        service = new PhotoSubmissionService(photos, submissions, objects, new PhotoSubmissionDecoderStub(),
            new PhotoSubmissionSettings(true, "lab-videos", true), clock, video: video);
    }

    [Fact]
    public async Task ContendedClaimSkipsStorageAndLetsTheWorkerContinueAsync()
    {
        jobs.Candidates.Enqueue(File(1));
        jobs.ClaimAccepted = false;

        Assert.True(await service.CleanNextAsync(Token));
        Assert.Single(jobs.Claims);
        Assert.Empty(multipart.CheckedKeys);
        Assert.Empty(jobs.Completed);
        Assert.Empty(jobs.Scheduled);
        Assert.False(await service.CleanNextAsync(Token));
    }

    [Fact]
    public async Task StorageFailureSchedulesOnlyItsJobAndTheNextJobCompletesAsync()
    {
        jobs.Candidates.Enqueue(File(1));
        jobs.Candidates.Enqueue(File(2, state: "expired"));
        multipart.OnListUploads = () => throw new PhotoStorageUnavailableException();

        Assert.True(await service.CleanNextAsync(Token));

        var first = Assert.Single(jobs.Claims);
        Assert.Equal(Deadline.AddHours(25), first.EligibleAt);
        Assert.True(first.LeaseUntil > first.Now);
        Assert.Equal((1L, first.Lease, clock.Now, clock.Now.AddMinutes(1), "video-multipart-storage-unavailable"),
            Assert.Single(jobs.Scheduled));
        Assert.Empty(jobs.Completed);

        multipart.OnListUploads = null;
        Assert.True(await service.CleanNextAsync(Token));

        Assert.Equal(Deadline.AddHours(24), jobs.Claims[1].EligibleAt);
        Assert.Equal((2L, jobs.Claims[1].Lease, clock.Now), Assert.Single(jobs.Completed));
    }

    [Fact]
    public async Task ChecksEveryAbortedUploadAndRetriesIfAnInFlightPartRemainsAsync()
    {
        jobs.Candidates.Enqueue(File(1));
        multipart.OpenUploads.Enqueue(["old-upload", "other-upload"]);
        multipart.OpenUploads.Enqueue([]);
        multipart.RemainingParts["old-upload"] = [new VideoPartModel(1, 10, new byte[32], "etag")];

        Assert.True(await service.CleanNextAsync(Token));

        Assert.Equal(["old-upload", "other-upload"], multipart.Aborted);
        Assert.Equal(multipart.Aborted, multipart.CheckedParts);
        Assert.Equal(["submissions/temporary/1", "submissions/temporary/1"], multipart.CheckedKeys);
        Assert.Equal("video-multipart-abort-pending", Assert.Single(jobs.Scheduled).Error);
        Assert.Empty(jobs.Completed);
    }

    [Fact]
    public async Task ProviderTimeoutSchedulesRetryWithSanitizedCodeAsync()
    {
        jobs.Candidates.Enqueue(File(1));
        multipart.OnListUploads = () => throw new OperationCanceledException("provider-specific detail");

        Assert.True(await service.CleanNextAsync(Token));

        Assert.Equal("video-multipart-cleanup-timeout", Assert.Single(jobs.Scheduled).Error);
        Assert.Empty(jobs.Completed);
    }

    [Fact]
    public async Task CallerCancellationLeavesTheLeaseForRecoveryAsync()
    {
        jobs.Candidates.Enqueue(File(1));
        using var cancellation = new CancellationTokenSource();
        multipart.OnListUploads = () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CleanNextAsync(cancellation.Token));

        Assert.Single(jobs.Claims);
        Assert.Empty(jobs.Completed);
        Assert.Empty(jobs.Scheduled);
    }

    private static PhotoSubmissionEntity File(long id, string state = "rejected") => new()
    {
        TemporaryObjectId = id,
        State = state,
        Bucket = "lab-videos",
        TemporaryKey = $"submissions/temporary/{id}",
        OriginalKey = $"submissions/original/{id}",
        DeclaredMediaType = "video/mp4",
        CompletionDeadlineAt = new DateTime(2032, 3, 1, 13, 0, 0),
        TerminalAt = Deadline.AddHours(1),
        Sha256 = new byte[32],
        PartSha256 = [new byte[32], new byte[32]]
    };
}
