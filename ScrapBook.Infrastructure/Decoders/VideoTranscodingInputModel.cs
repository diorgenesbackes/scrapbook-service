using ScrapBook.Domain.Models;

namespace ScrapBook.Infrastructure.Decoders;

internal sealed record VideoTranscodingInputModel(string Path, string MediaType, MediaVideoPropertiesModel Properties);
