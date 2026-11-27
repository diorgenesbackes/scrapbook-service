namespace ScrapBook.Domain.Models;

public sealed record MediaVideoPropertiesModel(string Container, string VideoCodec, string? AudioCodec, decimal Duration,
    int Width, int Height, int PixelAspectNumerator, int PixelAspectDenominator, int FrameRateNumerator,
    int FrameRateDenominator, int Rotation, bool VariableFrameRate, int AudioChannels, int AudioSampleRate,
    bool HasLocationMetadata, string? AudioProfile = null);
