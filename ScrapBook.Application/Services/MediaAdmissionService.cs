using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using static ScrapBook.Application.Services.MediaConfirmationService;

namespace ScrapBook.Application.Services;

public sealed class MediaAdmissionService(IMediaAdmissionRepository repository, IOutboxRepository outbox, TimeProvider clock)
{
    private const string Consumer = "media-preparation";
    private const string Source = "internal";

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
            return MatchesDelivery(prior.State, prior.PayloadHash, hash);
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
            return MatchesDelivery(existing.State, existing.PayloadHash, hash);
        }
        var now = clock.GetUtcNow();
        if (original!.ProcessingState != "pending" || now - message.ConfirmedAt > TimeSpan.FromDays(30))
        {
            return MediaAdmissionResult.Quarantined;
        }

        var photo = original.Kind == "photo";
        var inserted = await repository.InsertJobAsync(message, original.ProfileVersion, photo ? "photo_prepare" : "av_transcode",
            photo ? JsonSerializer.Serialize(PhotoProfile) : "{}", photo ? PhotoKinds : ["playback"], now, cancellationToken);
        if (!inserted)
        {
            return MediaAdmissionResult.Quarantined;
        }
        await repository.InsertDeliveryAsync(Consumer, Source, message, hash, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MediaAdmissionResult.Admitted;
    }

    private static MediaAdmissionResult MatchesDelivery(string state, byte[] persisted, byte[] incoming) =>
        state == "completed" && CryptographicOperations.FixedTimeEquals(persisted, incoming)
            ? MediaAdmissionResult.Duplicate : MediaAdmissionResult.Quarantined;
}
