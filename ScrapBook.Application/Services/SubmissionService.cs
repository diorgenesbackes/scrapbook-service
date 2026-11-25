using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class SubmissionService(ISubmissionRepository repository, ISubmissionProtectionCodec protection, TimeProvider clock)
{
    private const string Operation = "contribution.create";
    private const string FirstConfirmation = "event.first-record-confirmed.v1";
    private static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(30);

    public async Task<SubmissionNoticeResponse> GetNoticeAsync(string code, CancellationToken cancellationToken)
    {
        await FindEventAsync(code, false, cancellationToken);
        var notice = await FindNoticeAsync(clock.GetUtcNow(), cancellationToken);
        return new SubmissionNoticeResponse(notice.Code, notice.Body);
    }

    public async Task<SubmissionConsentResponse> ConfirmConsentAsync(string code, ConfirmSubmissionConsentRequest request,
        DateTimeOffset ingress, CancellationToken cancellationToken)
    {
        var item = await FindEventAsync(code, false, cancellationToken);
        var name = NormalizeName(request.GuestName, "guestName");
        EnsureWindow(item, ingress);
        var notice = await FindNoticeAsync(ingress, cancellationToken);
        if (!request.Accepted || notice.Code != request.NoticeCode)
        {
            throw new SubmissionConsentRequiredException();
        }

        var keyId = RequireEnabled();
        var issuedAt = clock.GetUtcNow();
        return new SubmissionConsentResponse(protection.CreateConsent(keyId, item.Id, notice.Id, name, issuedAt),
            issuedAt.Add(ConsentLifetime));
    }

    public async Task<SubmissionStatusResponse> GetAsync(string code, Guid attemptKey, byte[] secret, CancellationToken cancellationToken)
    {
        RequireEnabled();
        if (!IsCode(code)) throw new SubmissionAttemptNotFoundException();
        var item = await repository.FindEventAsync(code, false, cancellationToken) ?? throw new SubmissionAttemptNotFoundException();
        var attempt = await repository.FindAttemptAsync(item.Id, Operation, attemptKey, cancellationToken)
            ?? throw new SubmissionAttemptNotFoundException();
        AuthorizeAttempt(item.Id, attemptKey, secret, attempt, clock.GetUtcNow());
        return Represent(attempt);
    }

    public async Task<CreateSubmissionResponse> CreateAsync(string code, Guid attemptKey, byte[] secret,
        CreateSubmissionRequest request, DateTimeOffset ingress, DateTimeOffset receivedAt, Guid correlationId,
        CancellationToken cancellationToken)
    {
        var keyId = RequireEnabled();
        ValidateCode(code);
        var identification = request.Identification ?? throw new SubmissionConsentRequiredException();
        var name = NormalizeName(identification.GuestName, "identification.guestName");
        var text = NormalizeText(request);

        await using var transaction = await repository.BeginAsync(cancellationToken);
        var item = await FindEventAsync(code, true, cancellationToken);
        var existing = await repository.FindAttemptAsync(item.Id, Operation, attemptKey, cancellationToken);
        if (existing is not null)
        {
            AuthorizeAttempt(item.Id, attemptKey, secret, existing, ingress);
            var hash = protection.HashPayload(existing.HashKeyId!, item.Id, identification.NoticeCode ?? "", name, text);
            if (!Matches(existing.PayloadHash, hash))
            {
                throw new SubmissionKeyReusedException();
            }
            return new CreateSubmissionResponse(false, Represent(existing));
        }

        EnsureWindow(item, ingress);
        if (receivedAt > EventOpeningService.ToBrasiliaInstant(item.CompletionDeadlineAt))
        {
            throw new SubmissionDeadlineExceededException();
        }

        var notice = await FindNoticeAsync(ingress, cancellationToken);
        var consentedAt = RequireConsent(identification, item.Id, notice, name, ingress);
        var committedAt = clock.GetUtcNow();
        if (receivedAt < ingress) receivedAt = ingress;
        if (committedAt < receivedAt) committedAt = receivedAt;
        committedAt = new DateTimeOffset(committedAt.UtcTicks - committedAt.UtcTicks % 10, TimeSpan.Zero);
        var command = new ConfirmTextSubmissionCommand(item.Id, notice.Id, name, text,
            Civil(consentedAt), Civil(ingress), Civil(receivedAt), Civil(committedAt),
            item.SubmissionOpensAt, item.SubmissionClosesAt, item.CompletionDeadlineAt,
            attemptKey, Operation, keyId, protection.HashPayload(keyId, item.Id, notice.Code, name, text)!,
            protection.HashAuthorization(keyId, item.Id, attemptKey, secret)!, ingress.AddDays(30), committedAt);

        var messageId = FirstMessageId(item.Id);
        var first = item.LockedAt is null && !await repository.HasConfirmedAsync(item.Id, messageId, cancellationToken);
        var recordId = await repository.InsertConfirmedTextAsync(command, cancellationToken);
        if (first)
        {
            var payload = JsonSerializer.Serialize(new
            {
                eventId = item.Id.ToString(CultureInfo.InvariantCulture),
                recordId = recordId.ToString(CultureInfo.InvariantCulture),
                confirmedAt = committedAt
            });
            await repository.InsertOutboxAsync(
                new SubmissionOutboxEntity(messageId, FirstConfirmation, item.Id, payload, correlationId, committedAt),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreateSubmissionResponse(true, new SubmissionStatusResponse("confirmed", committedAt));
    }

    private async Task<EventEntity> FindEventAsync(string code, bool forUpdate, CancellationToken cancellationToken)
    {
        RequireEnabled();
        ValidateCode(code);
        return await repository.FindEventAsync(code, forUpdate, cancellationToken) ?? throw new PublicEventNotFoundException();
    }

    private async Task<SubmissionNoticeEntity> FindNoticeAsync(DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        return await repository.FindNoticeAsync(effectiveAt, cancellationToken) ?? throw new SubmissionUnavailableException();
    }

    private DateTimeOffset RequireConsent(SubmissionIdentificationRequest identification, long eventId, SubmissionNoticeEntity notice,
        string name, DateTimeOffset ingress)
    {
        if (identification.NoticeCode != notice.Code || identification.ConsentProof is not { Length: > 0 and <= 2048 } proof
            || protection.VerifyConsent(proof, eventId, notice.Id, name) is not { } issuedAt
            || issuedAt > ingress || ingress - issuedAt >= ConsentLifetime)
        {
            throw new SubmissionConsentRequiredException();
        }
        return issuedAt;
    }

    private void AuthorizeAttempt(long eventId, Guid attemptKey, byte[] secret, SubmissionAttemptEntity attempt, DateTimeOffset now)
    {
        if (now >= attempt.AuthorizationExpiresAt || attempt.HashKeyId is not { } keyId
            || !Matches(attempt.AuthorizationHash, protection.HashAuthorization(keyId, eventId, attemptKey, secret)))
        {
            throw new SubmissionAttemptNotFoundException();
        }
    }

    private string RequireEnabled() => protection.ActiveKeyId ?? throw new SubmissionUnavailableException();

    private static void ValidateCode(string code)
    {
        if (!IsCode(code))
        {
            throw new PublicEventNotFoundException();
        }
    }

    private static bool IsCode(string code) => code.Length == 43
        && code.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_');

    private static void EnsureWindow(EventEntity item, DateTimeOffset ingress)
    {
        var state = EventSubmissionWindowService.Describe(item.SubmissionOpensAt, item.SubmissionClosesAt,
            item.CompletionDeadlineAt, ingress).State;
        if (state != "open")
        {
            throw new SubmissionWindowException(state);
        }
    }

    private static string NormalizeName(string? name, string field)
    {
        if (name is null) throw new EventRequestValidationException(field);
        var start = 0;
        var end = name.Length;
        while (start < end && IsGuestSpace(name[start])) start++;
        while (start < end && IsGuestSpace(name[end - 1])) end--;
        var result = name[start..end];
        EnsureText(result, 120, field);
        return result;
    }

    private static string NormalizeText(CreateSubmissionRequest request)
    {
        if (request.Category != "testimonial") throw new EventRequestValidationException("category");
        if (request.Format != "text") throw new EventRequestValidationException("format");
        var text = request.Text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            ?? throw new EventRequestValidationException("text");
        EnsureText(text, 5000, "text");
        return text;
    }

    private static bool IsGuestSpace(char value) => value == '\uFEFF' || value != '\u0085' && char.IsWhiteSpace(value);

    private static void EnsureText(string value, int limit, string field)
    {
        if (value.All(IsGuestSpace)) throw new EventRequestValidationException(field);
        var span = value.AsSpan();
        var count = 0;
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out var rune, out var read) != OperationStatus.Done || rune.Value == 0 || ++count > limit)
            {
                throw new EventRequestValidationException(field);
            }
            span = span[read..];
        }
    }

    private static bool Matches(byte[]? expected, byte[]? actual) =>
        expected is not null && actual is not null && CryptographicOperations.FixedTimeEquals(expected, actual);

    private static DateTime Civil(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, EventOpeningService.Zone).DateTime;

    private static Guid FirstMessageId(long eventId) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes(FirstConfirmation + ":" + eventId.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));

    private static SubmissionStatusResponse Represent(SubmissionAttemptEntity attempt) =>
        new(attempt.State, EventOpeningService.ToBrasiliaInstant(attempt.ConfirmedAt ??
            throw new SubmissionAttemptNotFoundException()).ToUniversalTime());
}
