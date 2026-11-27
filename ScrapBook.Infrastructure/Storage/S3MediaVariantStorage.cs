using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Storage;

public sealed class S3MediaVariantStorage(IAmazonS3 client, IPhotoSubmissionStorage submissionStorage) : IMediaVariantStorage
{
    public Task<byte[]> ReadOriginalAsync(MediaSourceEntity source, CancellationToken cancellationToken) =>
        submissionStorage.ReadAsync(source.Bucket, source.ObjectKey, source.VersionId, checked((int)source.Size), cancellationToken);

    public async Task<MediaStoredVariantModel> StoreVerifiedAsync(string bucket, string key, PhotoVariantModel variant,
        CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(variant.Bytes);
        try
        {
            using var input = new MemoryStream(variant.Bytes, writable: false);
            var written = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = input,
                ContentType = "image/jpeg",
                ChecksumSHA256 = Convert.ToBase64String(hash),
                IfNoneMatch = "*"
            }, cancellationToken);
            if (written.HttpStatusCode != HttpStatusCode.OK || string.IsNullOrWhiteSpace(written.VersionId)
                || written.VersionId == "null")
            {
                throw new MediaVariantStorageException();
            }

            var metadata = await submissionStorage.ReadMetadataAsync(bucket, key, written.VersionId, cancellationToken);
            if (metadata is null || metadata.VersionId != written.VersionId || metadata.Size != variant.Bytes.LongLength)
            {
                throw new MediaVariantStorageException();
            }
            var verification = await S3VideoObjectReader.CopyAndHashAsync(client, bucket, key, metadata.VersionId,
                metadata.Size, Stream.Null, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(verification, hash))
            {
                throw new MediaOutputInvalidException();
            }
            return new MediaStoredVariantModel(bucket, key, metadata.VersionId, metadata.Size, verification, metadata.LastModifiedAt);
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException
            or PhotoStorageUnavailableException or IOException)
        {
            throw new MediaVariantStorageException(error);
        }
        catch (PhotoContentInvalidException)
        {
            throw new MediaOutputInvalidException();
        }
    }
}
