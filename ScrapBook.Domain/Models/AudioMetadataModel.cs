namespace ScrapBook.Domain.Models;

public sealed record AudioMetadataModel(string MediaType, string Container, string AudioCodec, decimal Duration,
    byte[] Sha256);
