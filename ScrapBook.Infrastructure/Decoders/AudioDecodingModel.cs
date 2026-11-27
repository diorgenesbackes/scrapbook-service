namespace ScrapBook.Infrastructure.Decoders;

internal sealed record AudioDecodingModel(string MediaType, string Container, decimal Duration, int StreamCount,
    int PrimaryIndex, AudioTrackTimingModel Primary);
