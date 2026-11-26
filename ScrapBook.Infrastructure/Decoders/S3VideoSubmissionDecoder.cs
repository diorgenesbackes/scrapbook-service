using Amazon.S3;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;
using ScrapBook.Infrastructure.Storage;

namespace ScrapBook.Infrastructure.Decoders;

public sealed class S3VideoSubmissionDecoder(IAmazonS3 storage, FfprobeVideoDecoder probe) : IVideoSubmissionDecoder, IDisposable
{
    private readonly SemaphoreSlim downloadSlot = new(1, 1);

    public async Task<VideoMetadataModel> DecodeAsync(string bucket, string key, string versionId, long expectedSize,
        CancellationToken cancellationToken)
    {
        DirectoryInfo? scratch = null;
        await downloadSlot.WaitAsync(cancellationToken);
        try
        {
            scratch = Directory.CreateTempSubdirectory("scrapbook-video-");
            var file = Path.Combine(scratch.FullName, "video");
            byte[] sha256;
            await using (var destination = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sha256 = await S3VideoObjectReader.CopyAndHashAsync(storage, bucket, key, versionId, expectedSize,
                    destination, cancellationToken);
            }
            return await probe.DecodeAsync(file, sha256, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PhotoDecoderUnavailableException();
        }
        finally
        {
            DeleteScratch(scratch);
            downloadSlot.Release();
        }
    }

    public void Dispose() => downloadSlot.Dispose();

    private static void DeleteScratch(DirectoryInfo? scratch)
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
