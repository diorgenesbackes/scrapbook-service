using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Requests;
using ScrapBook.Infrastructure.Codecs;

namespace ScrapBook.UnitTests.Services;

public sealed class SubmissionServiceTests
{
    private static readonly DateTimeOffset Opening = DateTimeOffset.Parse("2032-02-29T02:00:00Z");
    private static readonly DateTimeOffset Closing = DateTimeOffset.Parse("2032-03-01T15:00:00Z");
    private static readonly DateTimeOffset Deadline = Closing.AddHours(1);
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly SubmissionTestRepository repository = new();
    private readonly EventTestTimeProvider clock = new() { Now = Opening };
    private readonly SubmissionProtectionCodec codec = new("local", new Dictionary<string, byte[]> { ["local"] = new byte[32] });
    private readonly Guid key = Guid.NewGuid();
    private readonly byte[] secret = new byte[32];

    private SubmissionService Service => new(repository, codec, clock);

    [Fact]
    public async Task ConfirmsAtOpeningWithUnicodeAndFirstFactAsync()
    {
        var text = string.Concat(Enumerable.Repeat("🎉", 5000));
        var response = await CreateAsync(Request(Opening, text), Opening, Opening);
        Assert.True(response.Created);
        Assert.Equal(Opening, response.Status.ConfirmedAt);
        var inserted = Assert.IsType<ScrapBook.Domain.Commands.ConfirmTextSubmissionCommand>(repository.Inserted);
        Assert.Equal(text, inserted.Text);
        Assert.Equal("Convidada", inserted.GuestName);
        Assert.Equal(repository.Event.SubmissionOpensAt, inserted.SubmittedAt);
        Assert.Equal("event.first-record-confirmed.v1", repository.Outbox?.Type);
        Assert.DoesNotContain("Convidada", repository.Outbox!.Payload);
        Assert.True(repository.Transaction.Committed);
    }

    [Theory]
    [InlineData(-1, "before")]
    [InlineData(0, "closed")]
    public async Task RejectsBeforeOpeningAndAtClosingAsync(int milliseconds, string state)
    {
        var at = (state == "before" ? Opening : Closing).AddMilliseconds(milliseconds);
        var error = await Assert.ThrowsAsync<SubmissionWindowException>(() => CreateAsync(Request(at), at, at));
        Assert.Equal(state, error.WindowState);
        Assert.Null(repository.Inserted);
        Assert.False(repository.Transaction.Committed);
    }

    [Fact]
    public async Task AdmittedReceiptIncludesDeadlineEvenWhenCommitIsLaterAsync()
    {
        var started = Closing.AddMilliseconds(-1);
        clock.Now = Deadline.AddSeconds(2);
        var policy = await CreateAsync(Request(started), started, Deadline);
        Assert.Equal(clock.Now, policy.Status.ConfirmedAt);
        Assert.Equal(new DateTime(2032, 3, 1, 13, 0, 0), repository.Inserted!.ReceivedAt);
        await Assert.ThrowsAsync<SubmissionDeadlineExceededException>(
            () => CreateAsync(Request(started), started, Deadline.AddTicks(1)));
    }

    [Fact]
    public async Task ReplayAfterClosingRetainsAuthAndCanonicalContentAsync()
    {
        var request = Request(Opening, "linha\r\nseguinte");
        var original = await CreateAsync(request, Opening, Opening);
        var saved = repository.Inserted!;
        repository.Attempt = new SubmissionAttemptEntity
        {
            State = "confirmed", ConfirmedAt = saved.ConfirmedAt, HashKeyId = saved.HashKeyId,
            PayloadHash = saved.PayloadHash, AuthorizationHash = saved.AuthorizationHash,
            AuthorizationExpiresAt = saved.AuthorizationExpiresAt
        };
        repository.Notice = null;
        clock.Now = Closing.AddHours(3);
        var replay = await CreateAsync(request with { Text = "linha\nseguinte" }, clock.Now, clock.Now);
        Assert.False(replay.Created);
        Assert.Equal(original.Status, replay.Status);
        Assert.Equal(original.Status, await Service.GetAsync(SubmissionTestRepository.Code, key, secret, Token));
        await Assert.ThrowsAsync<SubmissionKeyReusedException>(
            () => CreateAsync(request with { Text = "alterado" }, clock.Now, clock.Now));
        clock.Now = saved.AuthorizationExpiresAt;
        await Assert.ThrowsAsync<SubmissionAttemptNotFoundException>(
            () => Service.GetAsync(SubmissionTestRepository.Code, key, secret, Token));
        await Assert.ThrowsAsync<SubmissionAttemptNotFoundException>(() => CreateAsync(request, clock.Now, clock.Now));
        Assert.Same(saved, repository.Inserted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("\u0000")]
    public async Task RejectsInvalidTextAsync(string value)
    {
        await Assert.ThrowsAsync<EventRequestValidationException>(() => CreateAsync(Request(Opening, value), Opening, Opening));
        Assert.Null(repository.Inserted);
    }

    [Fact]
    public async Task RejectsAboveTheScalarLimitAsync()
    {
        var text = string.Concat(Enumerable.Repeat("🎉", 5001));
        var error = await Assert.ThrowsAsync<EventRequestValidationException>(() => CreateAsync(Request(Opening, text), Opening, Opening));
        Assert.Equal("text", error.Field);
    }

    [Theory]
    [InlineData(6, 3, "Convidada", 0)]
    [InlineData(5, 4, "Convidada", 0)]
    [InlineData(5, 3, "Outro nome", 0)]
    [InlineData(5, 3, "Convidada", -30)]
    [InlineData(5, 3, "Convidada", 1)]
    public async Task ConsentBindsEventNoticeNameAndIssuanceAsync(long eventId, long noticeId, string name, int issuedOffset)
    {
        var request = Request(Opening);
        var proof = codec.CreateConsent("local", eventId, noticeId, name, Opening.AddMinutes(issuedOffset));
        await Assert.ThrowsAsync<SubmissionConsentRequiredException>(() =>
            CreateAsync(request with { Identification = request.Identification! with { ConsentProof = proof } }, Opening, Opening));
        Assert.Null(repository.Inserted);
    }

    [Fact]
    public async Task ConsentAndOutboxBehaveForLaterContributionsAsync()
    {
        repository.PriorConfirmation = true;
        var issued = await Service.ConfirmConsentAsync(SubmissionTestRepository.Code,
            new ConfirmSubmissionConsentRequest("Convidada", "fixture", true), Opening, Token);
        Assert.Equal(Opening.AddMinutes(30), issued.ExpiresAt);
        var request = Request(Opening);
        var result = await CreateAsync(request with { Identification = request.Identification! with { ConsentProof = issued.ConsentProof } },
            Opening, Opening);
        Assert.True(result.Created);
        Assert.Null(repository.Outbox);
    }

    private CreateSubmissionRequest Request(DateTimeOffset issuedAt, string text = "Uma lembrança") => new("testimonial", "text",
        new SubmissionIdentificationRequest("  Convidada  ", "fixture", codec.CreateConsent("local", 5, 3, "Convidada", issuedAt)), text);

    private async Task<ScrapBook.Domain.Responses.CreateSubmissionResponse> CreateAsync(CreateSubmissionRequest request,
        DateTimeOffset ingress, DateTimeOffset receivedAt) =>
        await Service.CreateAsync(SubmissionTestRepository.Code, key, secret, request, ingress, receivedAt, Guid.NewGuid(), Token);
}
