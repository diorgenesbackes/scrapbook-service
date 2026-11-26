using System.Security.Cryptography;
using ScrapBook.Application.Codecs;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

internal static class EventStructuralLockFactService
{
    internal const string Consumer = "event-structural-lock";
    internal const string Source = "internal";
    internal const string ReplayAuthorized = "REPLAY_AUTHORIZED";

    internal static FirstRecordConfirmedModel? ReadOriginal(OutboxMessageEntity? row) =>
        row is { AggregateType: "event", Type: FirstRecordConfirmedModel.Type, ProtocolVersion: FirstRecordConfirmedModel.Version }
            && FirstRecordConfirmedCodec.ReadOutbox(row) is { } message
            && message.MessageId == SubmissionService.FirstMessageId(message.EventId)
            ? message : null;

    internal static DateTime CivilConfirmation(FirstRecordConfirmedModel message) =>
        SubmissionService.Civil(FirstRecordConfirmedCodec.MicrosecondPrecision(message.ConfirmedAt));

    internal static async Task<bool> HasConfirmedRecordAsync(IEventStructuralLockRepository locks,
        FirstRecordConfirmedModel message, CancellationToken cancellationToken) =>
        await locks.FindRecordConfirmationAsync(message.EventId, message.RecordId, cancellationToken) == CivilConfirmation(message);

    internal static bool AcceptsLock(EventEntity item, FirstRecordConfirmedModel message) =>
        item.LockedAt is null || item.FirstRecordId == message.RecordId && item.FirstRecordConfirmedAt == CivilConfirmation(message);

    internal static bool MatchesHash(ProcessedMessageEntity processed, byte[] hash) =>
        CryptographicOperations.FixedTimeEquals(hash, processed.PayloadHash);

    internal static bool OutsideReplayHorizon(FirstRecordConfirmedModel message, DateTimeOffset now) =>
        now - message.ConfirmedAt > TimeSpan.FromDays(30);
}
