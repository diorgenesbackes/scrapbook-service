namespace ScrapBook.Domain.Models;

public sealed record VideoMetadataModel(string MediaType, string Container, string VideoCodec, string? AudioCodec,
    decimal Duration, int Width, int Height, int FrameRateNumerator, int FrameRateDenominator, int? Rotation, byte[] Sha256);
