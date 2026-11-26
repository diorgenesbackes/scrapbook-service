using System.Buffers;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Storage;

internal static class S3VideoObjectReader
{
    private const int BufferSize = 64 * 1024;

    public static async Task<byte[]> CopyAndHashAsync(IAmazonS3 storage, string bucket, string key, string versionId,
        long expectedSize, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await storage.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId
            }, cancellationToken);
            if (response.VersionId != versionId) throw new PhotoStorageUnavailableException();
            if (response.ContentLength != expectedSize || expectedSize <= 0) throw new PhotoContentInvalidException();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                await CopyExactlyAsync(response.ResponseStream, destination, hash, buffer.AsMemory(0, BufferSize),
                    expectedSize, cancellationToken);
                return hash.GetHashAndReset();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException or IOException)
        {
            throw new PhotoStorageUnavailableException(error);
        }
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, IncrementalHash hash, Memory<byte> buffer,
        long remaining, CancellationToken cancellationToken)
    {
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer.Span[..read]);
            await destination.WriteAsync(buffer[..read], cancellationToken);
            remaining -= read;
        }

        if (await source.ReadAsync(buffer[..1], cancellationToken) != 0) throw new PhotoContentInvalidException();
    }
}
