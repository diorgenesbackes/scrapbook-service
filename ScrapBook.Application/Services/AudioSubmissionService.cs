using System.Security.Cryptography;
using System.Text.Json;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Requests;

namespace ScrapBook.Application.Services;

public sealed class AudioSubmissionService(IAudioSubmissionDecoder decoder, IStorageObjectReader storage)
{
    private const int MaximumSize = 50_000_000;
    private const decimal MaximumDuration = 300;
    private const decimal MinimumStoredDuration = 0.000001m;

    internal static PhotoManifestModel NormalizeFile(PhotoSubmissionFileRequest file)
    {
        if (file.DeclaredMediaType is not ("audio/mpeg" or "audio/mp4" or "audio/wav" or "audio/ogg" or "audio/webm"))
        {
            throw new EventRequestValidationException("files[0].declaredMediaType");
        }
        if (file.SizeBytes is < 1 or > MaximumSize)
        {
            throw new EventRequestValidationException("files[0].sizeBytes");
        }
        if (file.PartSha256.ValueKind != JsonValueKind.Undefined)
        {
            throw new EventRequestValidationException("files[0].partSha256");
        }
        var sha256 = PhotoSubmissionService.ReadChecksum(file.Sha256, "files[0].sha256");
        return new PhotoManifestModel(file.FileKey, file.DeclaredMediaType, file.SizeBytes, sha256);
    }

    internal async Task<AudioMetadataModel> ValidateAsync(PhotoSubmissionEntity file, string version,
        CancellationToken cancellationToken)
    {
        var audio = await decoder.DecodeAsync(file.Bucket, file.TemporaryKey, version, file.DeclaredSize, cancellationToken);
        if (audio.MediaType != file.DeclaredMediaType || audio.Duration is <= 0 or > MaximumDuration
            || string.IsNullOrWhiteSpace(audio.AudioCodec)
            || !CryptographicOperations.FixedTimeEquals(audio.Sha256, file.Sha256))
        {
            throw new PhotoContentInvalidException();
        }

        return audio with { Duration = Math.Max(MinimumStoredDuration, decimal.Round(audio.Duration, 6)) };
    }

    internal async Task VerifyOriginalAsync(PhotoSubmissionEntity file, string version, CancellationToken cancellationToken)
    {
        var checksum = await storage.ReadSha256Async(file.Bucket, file.OriginalKey, version, file.DeclaredSize, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(checksum, file.Sha256))
        {
            throw new PhotoStorageUnavailableException();
        }
    }
}
