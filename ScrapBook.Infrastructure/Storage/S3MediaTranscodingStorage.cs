using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Storage;

internal sealed class S3MediaTranscodingStorage(IAmazonS3 client)
{
    private const string SourceVersion = "source-version";
    private const string SourceSha256 = "source-sha256";
    private const string InputSha256 = "input-sha256";

    public async Task<MediaStoredVariantModel> StageFileAsync(MediaSourceEntity source, string key, string mediaType,
        string originalPath, string inputPath, byte[] originalHash, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var contents = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (contents.Length <= 0 || contents.Length > maximumBytes) throw new MediaOutputInvalidException();

        var inputHash = inputPath == originalPath ? originalHash : await SHA256.HashDataAsync(contents, cancellationToken);
        contents.Position = 0;
        return await StageInputAsync(source, key, mediaType, contents, inputHash, cancellationToken);
    }

    public async Task<byte[]> ReadOriginalAsync(MediaSourceEntity source, string path, long maximumBytes,
        CancellationToken cancellationToken)
    {
        if ((source.Size <= 0 || source.Size > maximumBytes) || !IsVersionId(source.VersionId)
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

    public async Task<GetObjectMetadataResponse> ReadMetadataAsync(string bucket, string key, string? version,
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

    public async Task<byte[]> DownloadAsync(string bucket, string key, string version, long size, string path,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await S3VideoObjectReader.CopyAndHashAsync(client, bucket, key, version, size, destination, cancellationToken);
    }

    public static async Task<TResult> InScratchAsync<TResult>(string kind, Func<string, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        DirectoryInfo? scratch = null;
        try
        {
            scratch = Directory.CreateTempSubdirectory($"scrapbook-{kind}-transcode-");
            return await operation(Path.Combine(scratch.FullName, "original"), cancellationToken);
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
        }
    }

    public static MediaStoredVariantModel StoredVariant(string bucket, string key, GetObjectMetadataResponse metadata,
        byte[] hash) => new(bucket, key, metadata.VersionId, metadata.ContentLength, hash,
        new DateTimeOffset(metadata.LastModified!.Value.ToUniversalTime()));

    private static bool IsVersionId(string? value) => !string.IsNullOrWhiteSpace(value) && value != "null";

    public static void RequireKey(string key, string prefix, string filename, long? expectedFileId)
    {
        var parts = key.Split('/');
        if (parts is not ["submissions", var location, var fileId, var request, var name] || location != prefix
            || !long.TryParse(fileId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
            || expectedFileId is not null && expectedFileId != id
            || !Guid.TryParseExact(request, "N", out var requestKey) || requestKey == Guid.Empty || name != filename)
            throw new MediaOutputInvalidException();
    }
}
