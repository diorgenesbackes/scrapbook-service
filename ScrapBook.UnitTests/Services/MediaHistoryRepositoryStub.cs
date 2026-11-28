using ScrapBook.Domain.Entities;
using ScrapBook.Domain.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

internal sealed class MediaHistoryRepositoryStub : IMediaHistoryRepository
{
    public List<(long EventId, long AfterId, int Limit, DateTimeOffset HistoricalBefore, CancellationToken Token)> Pages { get; } = [];
    public List<(long EventId, long RecordId, long FileId, bool Locked, CancellationToken Token)> SourceReads { get; } = [];
    public List<(long EventId, long RecordId, CancellationToken Token)> ReceiptReads { get; } = [];
    public List<(long FileId, string Kind, int ProfileVersion, CancellationToken Token)> WorkReads { get; } = [];
    public List<(MediaConfirmationModel Confirmation, int ProfileVersion, string Kind, string[] Variants,
        DateTimeOffset Now, CancellationToken Token)> Jobs { get; } = [];
    public Dictionary<long, MediaSourceEntity> Sources { get; } = [];
    public MediaHistoryReceiptModel? Receipt { get; set; }
    public MediaHistoryWorkModel Work { get; set; } = new(null, null, null, null, false);
    public bool Inserted { get; set; } = true;

    public Task<IReadOnlyList<MediaHistoryCandidateModel>> FindPageAsync(long eventId, long afterId, int limit,
        DateTimeOffset historicalBefore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pages.Add((eventId, afterId, limit, historicalBefore, cancellationToken));
        IReadOnlyList<MediaHistoryCandidateModel> files = Sources.Values
            .Where(item => item.EventId == eventId && item.FileId > afterId)
            .OrderBy(item => item.FileId).Take(limit)
            .Select(item => new MediaHistoryCandidateModel(item.RecordId, item.FileId)).ToArray();
        return Task.FromResult(files);
    }

    public Task<MediaSourceEntity?> FindSourceAsync(long eventId, long recordId, long fileId, bool lockFile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceReads.Add((eventId, recordId, fileId, lockFile, cancellationToken));
        var source = Sources.GetValueOrDefault(fileId);
        return Task.FromResult(source?.EventId == eventId && source.RecordId == recordId ? source : null);
    }

    public Task<MediaHistoryReceiptModel?> FindReceiptAsync(long eventId, long recordId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReceiptReads.Add((eventId, recordId, cancellationToken));
        return Task.FromResult(Receipt);
    }

    public Task<MediaHistoryWorkModel> FindWorkAsync(long fileId, string kind, int profileVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkReads.Add((fileId, kind, profileVersion, cancellationToken));
        return Task.FromResult(Work);
    }

    public Task<bool> InsertJobAsync(MediaConfirmationModel confirmation, int profileVersion, string kind, string profile,
        string[] variantKinds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Jobs.Add((confirmation, profileVersion, kind, variantKinds, now, cancellationToken));
        return Task.FromResult(Inserted);
    }
}
