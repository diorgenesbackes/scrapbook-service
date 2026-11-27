namespace ScrapBook.Domain.Models;

public sealed record MediaAudioVariantModel(MediaStoredVariantModel Object, decimal Duration, string MediaType,
    string Container, string AudioCodec, int Channels, int SampleRate);
