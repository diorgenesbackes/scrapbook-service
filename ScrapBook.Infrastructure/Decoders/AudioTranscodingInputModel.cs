namespace ScrapBook.Infrastructure.Decoders;

internal sealed record AudioTranscodingInputModel(string Path, string MediaType, decimal Duration, int Channels);
