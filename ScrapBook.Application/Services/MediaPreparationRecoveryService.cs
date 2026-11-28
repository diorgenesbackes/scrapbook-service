using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.MediaConfirmationService;

namespace ScrapBook.Application.Services;

public sealed class MediaPreparationRecoveryService(IMediaPreparationRecoveryRepository recovery, IMediaAdmissionRepository admission,
    IMediaRecoveryAccessRepository audit, MediaRecoveryAccessService access, TimeProvider clock)
{
    public static bool IsValid(MediaReopenCommand command) =>
        command.EventId > 0 && command.FileId > 0 && command.ProfileVersion > 0 && command.ExpectedCycle > 0
        && MediaRecoveryAccessService.IsValidAudit(command.Incident, command.Revision);

    public static bool IsValid(MediaRedriveCommand command) =>
        command.EventId > 0 && command.MessageId != Guid.Empty
        && MediaRecoveryAccessService.IsValidAudit(command.Incident, command.Revision);

    public async Task<MediaRecoveryResult> ReopenAsync(MediaReopenCommand command, CancellationToken cancellationToken)
    {
        if (!IsValid(command)) return new("MEDIA_RECOVERY_INVALID_ARGUMENT", false);

        await using var transaction = await admission.BeginAsync(cancellationToken);
        var actor = await access.FindOperatorAsync(cancellationToken);
        if (actor is null) return new("MEDIA_RECOVERY_FORBIDDEN", false);

        var job = await recovery.FindJobAsync(command.EventId, command.FileId, command.ProfileVersion, true, cancellationToken);
        var result = job is null
            ? new("MEDIA_REOPEN_JOB_NOT_FOUND", false)
            : await ReopenJobAsync(command, job, cancellationToken);
        var applied = result.Succeeded && command.Apply;
        await audit.AuditAsync(new(actor, command.EventId, job?.FileId, "media.reopen",
            result.Succeeded ? "success" : "rejected", result.Code, command.Incident, command.Revision,
            Guid.NewGuid(), applied ? command.ExpectedCycle + 1 : job?.RetryCycle), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<MediaRecoveryResult> RedriveAsync(MediaRedriveCommand command, CancellationToken cancellationToken)
    {
        if (!IsValid(command)) return new("MEDIA_RECOVERY_INVALID_ARGUMENT", false);

        await using var transaction = await admission.BeginAsync(cancellationToken);
        var actor = await access.FindOperatorAsync(cancellationToken);
        if (actor is null) return new("MEDIA_RECOVERY_FORBIDDEN", false);

        var row = await recovery.FindFactAsync(command.EventId, command.MessageId, cancellationToken);
        var message = row is null ? null : MediaConfirmationCodec.ReadOutbox(row);
        var result = message is null
            ? new("MEDIA_REDRIVE_FACT_UNPROVEN", false)
            : await RedriveFactAsync(command, message, cancellationToken);
        await audit.AuditAsync(new(actor, command.EventId, message?.FileId, "media.redrive",
            result.Succeeded ? "success" : "rejected", result.Code, command.Incident, command.Revision,
            Guid.NewGuid(), MessageId: command.MessageId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<MediaRecoveryResult> ReopenJobAsync(MediaReopenCommand command, MediaRecoveryJobEntity job,
        CancellationToken cancellationToken)
    {
        if (job.State != "failed" || job.RetryCycle != command.ExpectedCycle || job.HasLease || job.HasActiveAttempt
            || job.RetryCycle == int.MaxValue || job.LastAttemptNumber == int.MaxValue)
        {
            return new("MEDIA_REOPEN_JOB_INELIGIBLE", false);
        }
        if (job.ConfirmationMessageId != MediaConfirmationCodec.MessageId(job.FileId)
            || job.ConfirmationCorrelationId is not { } correlationId || correlationId == Guid.Empty
            || job.ConfirmedAt is not { } confirmedAt)
        {
            return new("MEDIA_REOPEN_SOURCE_UNPROVEN", false);
        }

        var message = new MediaConfirmationModel(job.ConfirmationMessageId.Value, command.EventId, job.RecordId,
            job.FileId, confirmedAt, confirmedAt, correlationId);
        var original = await admission.FindSourceAsync(message, true, cancellationToken);
        if (!MatchesSource(message, original) || original!.ProfileVersion != command.ProfileVersion
            || original.ProcessingState != "failed" || original.Kind != job.FileKind
            || !await MatchesStoredFactAsync(message, cancellationToken))
        {
            return new("MEDIA_REOPEN_SOURCE_UNPROVEN", false);
        }

        var expectedVariants = job.FileKind == "photo" ? PhotoKinds : ["playback"];
        var failedVariants = await recovery.FindFailedVariantsAsync(command.EventId, command.FileId,
            command.ProfileVersion, cancellationToken);
        if (failedVariants is null || !failedVariants.SequenceEqual(expectedVariants))
        {
            return new("MEDIA_REOPEN_VARIANTS_INELIGIBLE", false);
        }
        if (!command.Apply) return new("MEDIA_REOPEN_PREVIEW", true);

        await recovery.ReopenAsync(command.EventId, job, command.ProfileVersion, command.ExpectedCycle + 1,
            expectedVariants, clock.GetUtcNow(), cancellationToken);
        return new("MEDIA_REOPEN_APPLIED", true);
    }

    private async Task<bool> MatchesStoredFactAsync(MediaConfirmationModel message, CancellationToken cancellationToken)
    {
        var row = await recovery.FindFactAsync(message.EventId, message.MessageId, cancellationToken);
        return row is null || MediaConfirmationCodec.ReadOutbox(row) is { } fact
            && fact.RecordId == message.RecordId && fact.FileId == message.FileId && fact.CorrelationId == message.CorrelationId
            && MediaConfirmationCodec.MicrosecondPrecision(fact.ConfirmedAt) == message.ConfirmedAt;
    }

    private async Task<MediaRecoveryResult> RedriveFactAsync(MediaRedriveCommand command, MediaConfirmationModel message,
        CancellationToken cancellationToken)
    {
        var original = await admission.FindSourceAsync(message, true, cancellationToken);
        if (!MatchesSource(message, original)) return new("MEDIA_REDRIVE_SOURCE_UNPROVEN", false);

        var hash = MediaConfirmationCodec.Hash(message);
        var delivery = await recovery.FindDeliveryAsync(command.EventId, command.MessageId, Consumer, Source, cancellationToken);
        if (delivery is not null && !MatchesDelivery(delivery, hash))
        {
            return new("MEDIA_REDRIVE_DELIVERY_CONFLICT", false);
        }

        var job = await recovery.FindJobAsync(command.EventId, message.FileId, original!.ProfileVersion, false, cancellationToken);
        var now = clock.GetUtcNow();
        if (job is not null)
        {
            if (!MatchesEffect(job, message)) return new("MEDIA_REDRIVE_EFFECT_UNPROVEN", false);
            if (delivery is not null) return new("MEDIA_REDRIVE_ALREADY_PROTECTED", true);
            if (!command.Apply) return new("MEDIA_REDRIVE_PROTECTION_PREVIEW", true);

            await admission.InsertDeliveryAsync(Consumer, Source, message, hash, now, cancellationToken);
            return new("MEDIA_REDRIVE_PROTECTION_RESTORED", true);
        }

        if (now - message.ConfirmedAt > ReplayHorizon || delivery is not null || original.ProcessingState != "pending")
        {
            return new("MEDIA_REDRIVE_EFFECT_UNPROVEN", false);
        }
        if (!command.Apply) return new("MEDIA_REDRIVE_ADMISSION_PREVIEW", true);

        return await MediaAdmissionService.PersistAsync(admission, message, original, hash, now, cancellationToken)
            ? new("MEDIA_REDRIVE_ADMITTED", true)
            : new("MEDIA_REDRIVE_EFFECT_UNPROVEN", false);
    }

    private static bool MatchesEffect(MediaRecoveryJobEntity job, MediaConfirmationModel message) =>
        job.ConfirmationMessageId == message.MessageId && job.ConfirmationCorrelationId == message.CorrelationId
        && job.ConfirmedAt == MediaConfirmationCodec.MicrosecondPrecision(message.ConfirmedAt)
        && job.RecordId == message.RecordId && job.FileId == message.FileId;
}
