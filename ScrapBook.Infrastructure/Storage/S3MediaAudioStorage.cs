using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Decoders;

namespace ScrapBook.Infrastructure.Storage;

public sealed class S3MediaAudioStorage(IAmazonS3 client, AudioTranscodingDecoder decoder) : IMediaAudioStorage, IDisposable
{
    private const long MaximumSourceBytes = 50_000_000;
    private const long MaximumInputBytes = 64_000_000;
    private const long MaximumPlaybackBytes = 10_000_000;
    private const string SourceVersion = "source-version";
    private const string SourceSha256 = "source-sha256";
    private const string InputSha256 = "input-sha256";
    private readonly SemaphoreSlim operationSlots = new(2, 2);

    public Task<MediaAudioInputModel> StageAsync(MediaSourceEntity source, string key, CancellationToken cancellationToken) =>
        InScratchAsync(async (path, token) =>
        {
            RequireKey(key, "transcode-input", "input", source.FileId);
            var originalHash = await ReadOriginalAsync(source, path, token);
            var input = await decoder.PrepareAsync(path, token);
            await using var contents = OpenRead(input.Path);
            if (contents.Length is <= 0 or > MaximumInputBytes) throw new MediaOutputInvalidException();
            var inputHash = input.Path == path ? originalHash : await SHA256.HashDataAsync(contents, token);
            contents.Position = 0;
            var written = await StageInputAsync(source, key, input.MediaType, contents, inputHash, token);
            return new MediaAudioInputModel(written, input.Duration, input.Channels);
        }, cancellationToken);

    public Task<MediaAudioVariantModel> VerifyAsync(string bucket, string key, CancellationToken cancellationToken) =>
        InScratchAsync(async (path, token) =>
        {
            RequireKey(key, "transcode-output", "playback.m4a", null);
            var metadata = await ReadMetadataAsync(bucket, key, null, token);
            if (metadata.ContentLength is <= 0 or > MaximumPlaybackBytes) throw new MediaOutputInvalidException();
            var hash = await DownloadAsync(bucket, key, metadata.VersionId, metadata.ContentLength, path, token);
            var audio = await decoder.InspectPlaybackAsync(path, token);
            var stored = StoredVariant(bucket, key, metadata, hash);
            return new MediaAudioVariantModel(stored, audio.Primary.Duration(false), audio.MediaType, audio.Container,
                audio.Primary.Codec, audio.Primary.Channels, audio.Primary.SampleRate);
        }, cancellationToken);

    public void Dispose() => operationSlots.Dispose();

    private async Task<byte[]> ReadOriginalAsync(MediaSourceEntity source, string path, CancellationToken cancellationToken)
    {
        if (source.Size is <= 0 or > MaximumSourceBytes || !IsVersionId(source.VersionId)
            || source.Sha256.Length != 32 || source.OriginalSha256 is not { Length: 32 } originalHash
            || !CryptographicOperations.FixedTimeEquals(source.Sha256, originalHash)) throw new MediaSourceIntegrityException();

        try
        {
            var hash = await DownloadAsync(source.Bucket, source.ObjectKey, source.VersionId, source.Size, path, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(hash, originalHash) ? hash : throw new MediaSourceIntegrityException();
        }
        catch (PhotoContentInvalidException)
        {
            throw new MediaSourceIntegrityException();
        }
    }

    private async Task<MediaStoredVariantModel> StageInputAsync(MediaSourceEntity source, string key, string mediaType,
        FileStream contents, byte[] hash, CancellationToken cancellationToken)
    {
        var size = contents.Length;
        var sourceHash = Convert.ToBase64String(source.Sha256);
        var inputHash = Convert.ToBase64String(hash);
        var write = new PutObjectRequest
        {
            BucketName = source.Bucket,
            Key = key,
            InputStream = contents,
            ContentType = mediaType,
            ChecksumSHA256 = inputHash,
            IfNoneMatch = "*"
        };
        write.Metadata[SourceVersion] = source.VersionId;
        write.Metadata[SourceSha256] = sourceHash;
        write.Metadata[InputSha256] = inputHash;
        var version = await WriteIfAbsentAsync(write, cancellationToken);
        var metadata = await ReadMetadataAsync(source.Bucket, key, version, cancellationToken);
        if (metadata.ContentLength != size || metadata.ContentType != mediaType
            || metadata.Metadata[SourceVersion] != source.VersionId || metadata.Metadata[SourceSha256] != sourceHash
            || metadata.Metadata[InputSha256] != inputHash) throw new MediaOutputInvalidException();

        var verified = await S3VideoObjectReader.CopyAndHashAsync(client, source.Bucket, key, metadata.VersionId,
            size, Stream.Null, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(verified, hash)) throw new MediaOutputInvalidException();

        return StoredVariant(source.Bucket, key, metadata, verified);
    }

    private async Task<string?> WriteIfAbsentAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.PutObjectAsync(request, cancellationToken);
            if (response.HttpStatusCode != HttpStatusCode.OK || !IsVersionId(response.VersionId))
                throw new MediaVariantStorageException();
            return response.VersionId;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    private async Task<GetObjectMetadataResponse> ReadMetadataAsync(string bucket, string key, string? version,
        CancellationToken cancellationToken)
    {
        var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = version
        }, cancellationToken);
        if (response.HttpStatusCode != HttpStatusCode.OK || !IsVersionId(response.VersionId)
            || version is not null && response.VersionId != version || response.DeleteMarker == "true"
            || response.LastModified is null || response.LastModified == DateTime.MinValue) throw new MediaVariantStorageException();
        return response;
    }

    private async Task<byte[]> DownloadAsync(string bucket, string key, string version, long size, string path,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await S3VideoObjectReader.CopyAndHashAsync(client, bucket, key, version, size, destination, cancellationToken);
    }

    private async Task<TResult> InScratchAsync<TResult>(Func<string, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        DirectoryInfo? scratch = null;
        await operationSlots.WaitAsync(cancellationToken);
        try
        {
            scratch = Directory.CreateTempSubdirectory("scrapbook-audio-transcode-");
            return await operation(Path.Combine(scratch.FullName, "audio"), cancellationToken);
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException
            or PhotoStorageUnavailableException or PhotoDecoderUnavailableException or PhotoDecoderBudgetException or IOException
            or UnauthorizedAccessException)
        {
            throw new MediaVariantStorageException(error);
        }
        catch (PhotoContentInvalidException)
        {
            throw new MediaOutputInvalidException();
        }
        finally
        {
            try
            {
                scratch?.Delete(recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            operationSlots.Release();
        }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static MediaStoredVariantModel StoredVariant(string bucket, string key, GetObjectMetadataResponse metadata,
        byte[] hash) => new(bucket, key, metadata.VersionId, metadata.ContentLength, hash,
        new DateTimeOffset(metadata.LastModified!.Value.ToUniversalTime()));

    private static bool IsVersionId(string? value) => !string.IsNullOrWhiteSpace(value) && value != "null";

    private static void RequireKey(string key, string prefix, string filename, long? expectedFileId)
    {
        var parts = key.Split('/');
        if (parts is not ["submissions", var location, var fileId, var request, var name] || location != prefix
            || !long.TryParse(fileId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
            || expectedFileId is not null && expectedFileId != id
            || !Guid.TryParseExact(request, "N", out var requestKey) || requestKey == Guid.Empty || name != filename)
            throw new MediaOutputInvalidException();
    }
}
