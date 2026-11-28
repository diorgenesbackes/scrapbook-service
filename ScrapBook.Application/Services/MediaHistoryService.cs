using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.MediaConfirmationService;

namespace ScrapBook.Application.Services;

public sealed class MediaHistoryService(IMediaHistoryRepository repository, IMediaAdmissionRepository admission,
    IOutboxRepository outbox, IMediaRecoveryAccessRepository audit, MediaRecoveryAccessService access, TimeProvider clock)
{
    public async Task<MediaRecoveryBatchResult> ScanAsync(MediaRecoveryBatchCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MediaRecoveryAccessService.IsValid(command))
        {
            return new("MEDIA_RECOVERY_INVALID_ARGUMENT", command.AfterId, 0, 0, 0, 0);
        }
        if (await access.FindOperatorAsync(cancellationToken) is not { } actor)
        {
            return new("MEDIA_RECOVERY_FORBIDDEN", command.AfterId, 0, 0, 0, 0);
        }

        var files = await repository.FindPageAsync(command.EventId, command.AfterId, command.Limit, cancellationToken);
        var changed = 0;
        var rejected = 0;
        var deferred = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await ScanFileAsync(command, actor, file, cancellationToken))
            {
                case MediaHistoryFileResult.Admitted: changed++; break;
                case MediaHistoryFileResult.Rejected: rejected++; break;
                case MediaHistoryFileResult.Simulated: deferred++; break;
            }
        }
        return new("OK", files.LastOrDefault()?.FileId ?? command.AfterId, files.Count, changed, rejected, deferred);
    }

    private async Task<MediaHistoryFileResult> ScanFileAsync(MediaRecoveryBatchCommand command, string actor,
        MediaHistoryCandidateModel file, CancellationToken cancellationToken)
    {
        await using var transaction = command.Apply ? await admission.BeginAsync(cancellationToken) : null;
        var messageId = MediaConfirmationCodec.MessageId(file.FileId);
        var correlationId = TechnicalCorrelation(messageId);
        var source = await repository.FindSourceAsync(command.EventId, file.RecordId, file.FileId, command.Apply,
            cancellationToken);
        if (source is null) return await RejectAsync("MEDIA_HISTORY_SOURCE_UNVERIFIED");

        var (confirmation, evidence) = await FindConfirmationAsync(command.EventId, file, source, messageId, cancellationToken);
        if (confirmation is null) return await RejectAsync(evidence);
        if (!MatchesSource(confirmation, source)) return await RejectAsync("MEDIA_HISTORY_SOURCE_UNVERIFIED");

        correlationId = confirmation.CorrelationId;
        var photo = source.Kind == "photo";
        var kind = photo ? "photo_prepare" : "av_transcode";
        var work = await repository.FindWorkAsync(file.FileId, kind, source.ProfileVersion, cancellationToken);
        var hash = MediaConfirmationCodec.Hash(confirmation);
        var delivery = await admission.FindDeliveryAsync(Consumer, Source, messageId, cancellationToken);
        if (work.JobId is not null)
        {
            if (work.MessageId is null) return await RejectAsync("MEDIA_HISTORY_LEGACY_JOB_UNVERIFIED");
            if (work.MessageId == messageId && work.CorrelationId == confirmation.CorrelationId
                && work.ConfirmedAt == MediaConfirmationCodec.MicrosecondPrecision(confirmation.ConfirmedAt)
                && MatchesDelivery(delivery, hash))
            {
                return MediaHistoryFileResult.AlreadyAdmitted;
            }
            return await RejectAsync("MEDIA_HISTORY_JOB_IDENTITY_CONFLICT");
        }
        if (delivery is not null || work.HasVariants) return await RejectAsync("MEDIA_HISTORY_WORK_CONFLICT");
        if (source.ProcessingState != "pending") return await RejectAsync("MEDIA_HISTORY_STATE_CONFLICT");
        if (transaction is null) return MediaHistoryFileResult.Simulated;

        var now = clock.GetUtcNow();
        var inserted = await repository.InsertJobAsync(confirmation, source.ProfileVersion, kind,
            photo ? JsonSerializer.Serialize(PhotoProfile) : "{}", photo ? PhotoKinds : ["playback"], now, cancellationToken);
        if (!inserted) return await RejectAsync("MEDIA_HISTORY_WORK_CONFLICT");

        await admission.InsertDeliveryAsync(Consumer, Source, confirmation, hash, now, cancellationToken);
        await AuditAsync(command, actor, file.FileId, correlationId, "success", evidence, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MediaHistoryFileResult.Admitted;

        async Task<MediaHistoryFileResult> RejectAsync(string reason)
        {
            if (transaction is not null)
            {
                await AuditAsync(command, actor, file.FileId, correlationId, "rejected", reason, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return MediaHistoryFileResult.Rejected;
        }
    }

    private async Task<(MediaConfirmationModel? Confirmation, string Evidence)> FindConfirmationAsync(long eventId,
        MediaHistoryCandidateModel file, MediaSourceEntity source, Guid messageId, CancellationToken cancellationToken)
    {
        if (await outbox.FindAsync(messageId, cancellationToken) is { } row)
        {
            var fact = MediaConfirmationCodec.ReadOutbox(row);
            return fact is not null && fact.EventId == eventId && fact.RecordId == file.RecordId && fact.FileId == file.FileId
                ? (fact, "MEDIA_HISTORY_OUTBOX") : (null, "MEDIA_HISTORY_OUTBOX_CONFLICT");
        }

        var receipt = await repository.FindReceiptAsync(eventId, file.RecordId, cancellationToken);
        if (receipt is not { Operation: "contribution.create", OperationState: "confirmed", SessionState: "confirmed",
                CompletedAt: { } completedAt } || receipt.TerminalAt != completedAt || receipt.Format != source.Kind
            || receipt.CorrelationId == Guid.Empty)
        {
            return (null, "MEDIA_HISTORY_RECEIPT_UNVERIFIED");
        }
        var instant = completedAt.ToUniversalTime();
        var correlation = receipt.CorrelationId ?? TechnicalCorrelation(messageId);
        return (new(messageId, eventId, file.RecordId, file.FileId, instant, instant, correlation),
            receipt.CorrelationId is null ? "MEDIA_HISTORY_RECEIPT_TECHNICAL_CORRELATION" : "MEDIA_HISTORY_RECEIPT");
    }

    private Task AuditAsync(MediaRecoveryBatchCommand command, string actor, long fileId, Guid correlationId,
        string outcome, string reason, CancellationToken cancellationToken) =>
        audit.AuditAsync(new(actor, command.EventId, fileId, "media.history", outcome, reason, command.Incident,
            command.Revision, correlationId), cancellationToken);

    private static Guid TechnicalCorrelation(Guid messageId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("media.history.correlation:" + messageId.ToString("D"))).AsSpan(0, 16));
}
