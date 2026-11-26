using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Requests;
using ScrapBook.Domain.Responses;

namespace ScrapBook.Application.Services;

public sealed class SubmissionService(ISubmissionRepository repository, ISubmissionProtectionCodec protection, TimeProvider clock,
    PhotoSubmissionService? photos = null)
{
    private const string Operation = "contribution.create";
    internal const string FirstConfirmation = "event.first-record-confirmed.v1";
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
        var attempt = await FindAuthorizedAttemptAsync(code, attemptKey, secret, cancellationToken);
        return Represent(attempt);
    }

    public Task<PhotoUploadAuthorizationResponse> AuthorizePhotoAsync(string code, Guid attemptKey, Guid fileKey,
        byte[] secret, CancellationToken cancellationToken) =>
        AuthorizeUploadAsync(code, attemptKey, fileKey, secret, null, cancellationToken);

    public async Task<PhotoUploadAuthorizationResponse> AuthorizeUploadAsync(string code, Guid attemptKey, Guid fileKey,
        byte[] secret, int? partNumber, CancellationToken cancellationToken)
    {
        var attempt = await FindAuthorizedAttemptAsync(code, attemptKey, secret, cancellationToken);
        return await RequireMediaService(attempt.Format).AuthorizeAsync(attempt.RecordId, fileKey,
            attempt.AuthorizationExpiresAt, partNumber, cancellationToken);
    }

    public async Task<MediaUploadStatusResponse> GetUploadStatusAsync(string code, Guid attemptKey, Guid fileKey, byte[] secret,
        CancellationToken cancellationToken)
    {
        var attempt = await FindAuthorizedAttemptAsync(code, attemptKey, secret, cancellationToken);
        return await RequireMediaService(attempt.Format).GetUploadStatusAsync(attempt.RecordId, fileKey, cancellationToken);
    }

    public Task<SubmissionStatusResponse> SignalPhotoCompletionAsync(string code, Guid attemptKey, byte[] secret,
        CancellationToken cancellationToken) =>
        SignalCompletionAsync(code, attemptKey, secret, cancellationToken);

    public async Task<SubmissionStatusResponse> SignalCompletionAsync(string code, Guid attemptKey, byte[] secret,
        CancellationToken cancellationToken)
    {
        var attempt = await FindAuthorizedAttemptAsync(code, attemptKey, secret, cancellationToken);
        if (attempt.Format is not ("photo" or "video" or "audio")) throw new SubmissionAttemptNotFoundException();
        if (attempt.Format == "video" && attempt.State == "receiving")
        {
            await RequireMediaService(attempt.Format).SignalVideoCompletionAsync(attempt.RecordId, cancellationToken);
            attempt = await FindAuthorizedAttemptAsync(code, attemptKey, secret, cancellationToken);
        }
        return Represent(attempt);
    }

    private async Task<SubmissionAttemptEntity> FindAuthorizedAttemptAsync(string code, Guid attemptKey, byte[] secret,
        CancellationToken cancellationToken)
    {
        RequireEnabled();
        if (!IsCode(code)) throw new SubmissionAttemptNotFoundException();
        var item = await repository.FindEventAsync(code, false, cancellationToken) ?? throw new SubmissionAttemptNotFoundException();
        var attempt = await repository.FindAttemptAsync(item.Id, Operation, attemptKey, cancellationToken)
            ?? throw new SubmissionAttemptNotFoundException();
        AuthorizeAttempt(item.Id, attemptKey, secret, attempt, clock.GetUtcNow());
        return attempt;
    }

    public async Task<CreateSubmissionResponse> CreateAsync(string code, Guid attemptKey, byte[] secret,
        CreateSubmissionRequest request, DateTimeOffset ingress, DateTimeOffset receivedAt, Guid correlationId,
        CancellationToken cancellationToken)
    {
        var keyId = RequireEnabled();
        ValidateCode(code);
        await using var transaction = await repository.BeginAsync(cancellationToken);
        var isMedia = request.Format is "photo" or "video" or "audio";
        var item = isMedia
            ? await repository.FindEventForShareAsync(code, cancellationToken) ?? throw new PublicEventNotFoundException()
            : await FindEventAsync(code, true, cancellationToken);
        if (isMedia)
        {
            await repository.LockPhotoAttemptAsync(item.Id, attemptKey, cancellationToken);
        }

        var existing = await repository.FindAttemptAsync(item.Id, Operation, attemptKey, cancellationToken);
        if (existing is not null) AuthorizeAttempt(item.Id, attemptKey, secret, existing, ingress);

        var identification = request.Identification ?? throw new SubmissionConsentRequiredException();
        var name = NormalizeName(identification.GuestName, "identification.guestName");
        if (request.Category is not ("testimonial" or "moment")) throw new EventRequestValidationException("category");
        var (text, caption, files, canonicalPayload) = NormalizePayload(request);
        if (existing is not null)
        {
            var hash = protection.HashPayload(existing.HashKeyId!, item.Id, identification.NoticeCode ?? "", name, canonicalPayload);
            if (!Matches(existing.PayloadHash, hash))
            {
                throw new SubmissionKeyReusedException();
            }
            return new CreateSubmissionResponse(false, Represent(existing));
        }

        EnsureWindow(item, ingress);
        if (files is null && receivedAt > EventOpeningService.ToBrasiliaInstant(item.CompletionDeadlineAt))
        {
            throw new SubmissionDeadlineExceededException();
        }

        var notice = await FindNoticeAsync(ingress, cancellationToken);
        var consentedAt = RequireConsent(identification, item.Id, notice, name, ingress);
        if (files is not null)
        {
            var status = await RequireMediaService(request.Format).AdmitAsync(item, notice.Id, request.Category,
                request.Format!, name, caption, files, consentedAt, ingress, Operation, attemptKey, keyId,
                protection.HashPayload(keyId, item.Id, notice.Code, name, canonicalPayload)!,
                protection.HashAuthorization(keyId, item.Id, attemptKey, secret)!, correlationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CreateSubmissionResponse(true, status);
        }

        var committedAt = clock.GetUtcNow();
        if (receivedAt < ingress) receivedAt = ingress;
        if (committedAt < receivedAt) committedAt = receivedAt;
        committedAt = new DateTimeOffset(committedAt.UtcTicks - committedAt.UtcTicks % 10, TimeSpan.Zero);
        var command = new ConfirmTextSubmissionCommand(item.Id, notice.Id, name, text!,
            Civil(consentedAt), Civil(ingress), Civil(receivedAt), Civil(committedAt),
            item.SubmissionOpensAt, item.SubmissionClosesAt, item.CompletionDeadlineAt,
            attemptKey, Operation, keyId, protection.HashPayload(keyId, item.Id, notice.Code, name, canonicalPayload)!,
            protection.HashAuthorization(keyId, item.Id, attemptKey, secret)!, ingress.AddDays(30), committedAt);

        var messageId = FirstMessageId(item.Id);
        var first = item.LockedAt is null && !await repository.HasConfirmedAsync(item.Id, messageId, cancellationToken);
        var recordId = await repository.InsertConfirmedTextAsync(command, cancellationToken);
        if (first)
        {
            await repository.InsertOutboxAsync(FirstConfirmationMessage(item.Id, recordId, correlationId, committedAt),
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

    private static (string? Text, string? Caption, IReadOnlyList<PhotoManifestModel>? Files, string CanonicalPayload) NormalizePayload(
        CreateSubmissionRequest request)
    {
        if (request.Format is "photo" or "video" or "audio")
        {
            return PhotoSubmissionService.NormalizePayload(request);
        }
        if (request.Format != "text" || request.Category != "testimonial") throw new EventRequestValidationException("format");
        if (request.Caption is not null || request.Files is not null) throw new EventRequestValidationException("format");
        var text = NormalizeLineEndings(request.Text) ?? throw new EventRequestValidationException("text");
        EnsureText(text, 5000, "text");
        return (text, null, null, text);
    }

    private PhotoSubmissionService RequireMediaService(string? format) => format switch
    {
        "photo" => photos ?? throw new PhotoSubmissionDisabledException(),
        "video" => photos ?? throw new VideoSubmissionDisabledException(),
        "audio" => photos ?? throw new AudioSubmissionDisabledException(),
        _ => throw new SubmissionAttemptNotFoundException()
    };

    internal static string? NormalizeLineEndings(string? text) =>
        text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool IsGuestSpace(char value) => value == '\uFEFF' || value != '\u0085' && char.IsWhiteSpace(value);

    internal static void EnsureText(string value, int limit, string field, bool requireContent = true)
    {
        if (requireContent && value.All(IsGuestSpace)) throw new EventRequestValidationException(field);
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

    internal static DateTime Civil(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, EventOpeningService.Zone).DateTime;

    internal static Guid FirstMessageId(long eventId) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes(FirstConfirmation + ":" + eventId.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));

    internal static SubmissionOutboxEntity FirstConfirmationMessage(long eventId, long recordId, Guid correlationId,
        DateTimeOffset confirmedAt)
    {
        var payload = JsonSerializer.Serialize(new
        {
            eventId = eventId.ToString(CultureInfo.InvariantCulture),
            recordId = recordId.ToString(CultureInfo.InvariantCulture),
            confirmedAt
        });
        return new SubmissionOutboxEntity(FirstMessageId(eventId), FirstConfirmation, eventId, payload, correlationId, confirmedAt);
    }

    private SubmissionStatusResponse Represent(SubmissionAttemptEntity attempt)
    {
        if (attempt.ConfirmedAt is { } confirmedAt)
        {
            return new SubmissionStatusResponse("confirmed", EventOpeningService.ToBrasiliaInstant(confirmedAt).ToUniversalTime());
        }
        var deadline = attempt.CompletionDeadlineAt is { } completionDeadline && attempt.State == "receiving"
            ? EventOpeningService.ToBrasiliaInstant(completionDeadline).ToUniversalTime()
            : (DateTimeOffset?)null;
        if ((attempt.Category == "moment" || attempt.Format is "video" or "audio") && deadline is { } cutoff && clock.GetUtcNow() >= cutoff)
        {
            return new SubmissionStatusResponse("reconciling");
        }
        var pending = attempt.Category == "moment" && attempt.Format == "photo" && attempt.State == "receiving"
            ? attempt.PendingPositions : null;
        var problems = attempt.Category == "moment" && attempt.State is "rejected" or "expired"
            && attempt.ProblemPositions is { } positions && attempt.ProblemCodes is { } codes
            ? positions.Zip(codes, (position, code) => new PhotoPositionProblemModel(position, code)).ToArray()
            : null;
        return new SubmissionStatusResponse(attempt.State, CompletionDeadlineAt: deadline, Code: attempt.ResultCode,
            PendingPositions: pending, Problems: problems);
    }
}
