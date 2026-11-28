using System.Text.Json;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.MediaConfirmationService;

namespace ScrapBook.Application.Services;

public sealed class MediaAdmissionService(IMediaAdmissionRepository repository, IOutboxRepository outbox, TimeProvider clock)
{
    public async Task<MediaAdmissionResult> AdmitAsync(string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MediaConfirmationCodec.ReadEnvelope(body) is not { } message)
        {
            return MediaAdmissionResult.Quarantined;
        }
        var hash = MediaConfirmationCodec.Hash(message);
        if (await repository.FindDeliveryAsync(Consumer, Source, message.MessageId, cancellationToken) is { } prior)
        {
            return DuplicateResult(prior, hash);
        }
        var row = await outbox.FindAsync(message.MessageId, cancellationToken);
        if (row is null || MediaConfirmationCodec.ReadOutbox(row) != message)
        {
            return MediaAdmissionResult.Quarantined;
        }

        await using var transaction = await repository.BeginAsync(cancellationToken);
        var original = await repository.FindSourceAsync(message, true, cancellationToken);
        if (!MatchesSource(message, original))
        {
            return MediaAdmissionResult.Quarantined;
        }
        if (await repository.FindDeliveryAsync(Consumer, Source, message.MessageId, cancellationToken) is { } existing)
        {
            return DuplicateResult(existing, hash);
        }
        var now = clock.GetUtcNow();
        if (original!.ProcessingState != "pending" || now - message.ConfirmedAt > ReplayHorizon)
        {
            return MediaAdmissionResult.Quarantined;
        }

        if (!await PersistAsync(repository, message, original, hash, now, cancellationToken))
        {
            return MediaAdmissionResult.Quarantined;
        }
        await transaction.CommitAsync(cancellationToken);
        return MediaAdmissionResult.Admitted;
    }

    internal static async Task<bool> PersistAsync(IMediaAdmissionRepository repository, MediaConfirmationModel message,
        MediaSourceEntity original, byte[] hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var photo = original.Kind == "photo";
        var inserted = await repository.InsertJobAsync(message, original.ProfileVersion, photo ? "photo_prepare" : "av_transcode",
            photo ? JsonSerializer.Serialize(PhotoProfile) : "{}", photo ? PhotoKinds : ["playback"], now, cancellationToken);
        if (inserted)
        {
            await repository.InsertDeliveryAsync(Consumer, Source, message, hash, now, cancellationToken);
        }
        return inserted;
    }

    private static MediaAdmissionResult DuplicateResult(ProcessedMessageEntity delivery, byte[] hash) =>
        MatchesDelivery(delivery, hash)
            ? MediaAdmissionResult.Duplicate : MediaAdmissionResult.Quarantined;
}
