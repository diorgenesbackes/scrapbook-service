using System.Text.Json;

namespace ScrapBook.Infrastructure.Decoders;

internal static class MediaLocationMetadata
{
    public static bool ContainsLocation(JsonElement media) => media.TryGetProperty("tags", out var tags)
        && tags.EnumerateObject().Any(tag => IsLocationTag(tag.Name));

    public static bool IsLocationTag(string name) =>
        name.Contains("location", StringComparison.OrdinalIgnoreCase)
        || name.Contains("latitude", StringComparison.OrdinalIgnoreCase)
        || name.Contains("longitude", StringComparison.OrdinalIgnoreCase)
        || name.Contains("altitude", StringComparison.OrdinalIgnoreCase)
        || name.Contains("gps", StringComparison.OrdinalIgnoreCase)
        || name.Contains("iso6709", StringComparison.OrdinalIgnoreCase)
        || name.Contains("geotag", StringComparison.OrdinalIgnoreCase)
        || name.Equals("loci", StringComparison.OrdinalIgnoreCase)
        || name.Equals("©xyz", StringComparison.OrdinalIgnoreCase);
}
