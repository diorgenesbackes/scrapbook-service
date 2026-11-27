namespace ScrapBook.Domain.Models;

public sealed record MediaVideoProfileModel(int Width, int Height, int FrameRateNumerator, int FrameRateDenominator,
    int Rotation);
