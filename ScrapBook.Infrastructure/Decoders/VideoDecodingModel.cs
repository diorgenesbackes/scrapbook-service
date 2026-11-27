using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

internal sealed record VideoDecodingModel(MediaVideoPropertiesModel Properties, string MediaType, int VideoIndex,
    int? AudioIndex, int StreamCount);
