using ScrapBook.Domain.Commands;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Application.Services;

public sealed class MediaCleanupService(MediaRecoveryAccessService access, IMediaRecoveryAccessRepository audit,
    IMediaCleanupRepository repository, IMediaCleanupStorage storage, TimeProvider clock, string bucket)
{
    public async Task<MediaRecoveryBatchResult> ScanAsync(MediaRecoveryBatchCommand command,
        CancellationToken cancellationToken)
    {
        if (!MediaRecoveryAccessService.IsValid(command)) return new("invalid_request", command.AfterId, 0, 0, 0, 0);
        var principal = await access.FindOperatorAsync(cancellationToken);
        if (principal is null) return new("forbidden", command.AfterId, 0, 0, 0, 0);

        var now = clock.GetUtcNow();
        var cutoff = now.AddHours(-24);
        var candidates = await repository.FindCandidatesAsync(command.EventId, command.AfterId, command.Limit,
            cutoff, cancellationToken);
        var changed = 0;
        var rejected = 0;
        var deferred = 0;
        foreach (var candidate in candidates)
        {
            var entry = new MediaRecoveryAuditModel(principal, command.EventId, candidate.FileId, "media.cleanup",
                "success", "cleanup_claimed", command.Incident, command.Revision, candidate.RequestKey);
            if (!IsEligible(candidate))
            {
                rejected++;
                if (command.Apply) await audit.AuditAsync(entry with { Outcome = "rejected", ReasonCode = "artifact_scope_mismatch" }, cancellationToken);
            }
            else if (command.Apply)
            {
                if (await TryDeleteAsync(candidate, entry, cutoff, now, cancellationToken)) changed++;
                else deferred++;
            }
        }

        var next = candidates.Count == 0 ? command.AfterId : candidates[^1].Id;
        return new(command.Apply ? "applied" : "preview", next, candidates.Count, changed, rejected, deferred);
    }

    private async Task<bool> TryDeleteAsync(MediaCleanupObjectModel candidate, MediaRecoveryAuditModel entry,
        DateTimeOffset cutoff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        long? version;
        await using (var transaction = await repository.BeginAsync(cancellationToken))
        {
            version = await repository.ClaimAsync(entry.EventId, candidate, cutoff, now, cancellationToken);
            if (version is null) return false;
            await audit.AuditAsync(entry, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            if (!await storage.DeleteExpiredVersionsAsync(candidate.Bucket, candidate.ObjectKey, cutoff, budget.Token))
                return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            return false;
        }
        catch (MediaVariantStorageException)
        {
            return false;
        }

        await using var completion = await repository.BeginAsync(cancellationToken);
        if (!await repository.CompleteAsync(candidate.Id, version.Value, clock.GetUtcNow(), cancellationToken)) return false;
        await audit.AuditAsync(entry with { ReasonCode = "expired_attempt_versions_removed" }, cancellationToken);
        await completion.CommitAsync(cancellationToken);
        return true;
    }

    private bool IsEligible(MediaCleanupObjectModel candidate)
    {
        if (candidate.Bucket != bucket || candidate.RequestKey == Guid.Empty) return false;
        var path = FormattableString.Invariant($"{candidate.FileId}/{candidate.RequestKey:N}/");
        if (candidate.Provider == "local" && candidate.FileKind == "photo")
        {
            var prefix = "submissions/derived/" + path;
            return candidate.AttemptState == "failed" && candidate.OutputPrefix == prefix
                && (candidate.ObjectKey == prefix + "image.jpg" || candidate.ObjectKey == prefix + "thumbnail.jpg");
        }

        if (candidate.Provider != "mediaconvert" || candidate.FileKind is not ("audio" or "video")) return false;
        var output = "submissions/transcode-output/" + path;
        if (candidate.OutputPrefix != output) return false;
        var playback = candidate.FileKind == "video" ? "playback.mp4" : "playback.m4a";
        return candidate.ObjectKey == "submissions/transcode-input/" + path + "input"
            || candidate.AttemptState == "failed" && candidate.ObjectKey == output + playback;
    }
}
