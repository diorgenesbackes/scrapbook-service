using System.Globalization;
using System.Text.Json;
using ScrapBook.Domain.Exceptions;
using static ScrapBook.Infrastructure.Decoders.FfprobeVideoDecoder;

namespace ScrapBook.Infrastructure.Decoders;

internal static class VideoDisplayMatrixReader
{
    public static int Rotation(JsonElement stream)
    {
        int? rotation = null;
        if (stream.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (Text(entry, "side_data_type") != "Display Matrix") continue;
                if (rotation is not null) throw new PhotoContentInvalidException();

                rotation = Normalize(RequiredRotation(entry));
                RequireOrthogonalMatrix(Text(entry, "displaymatrix"), rotation.Value);
            }
        }

        if (stream.TryGetProperty("tags", out var tags) && Text(tags, "rotate") is { } rotateTag)
        {
            if (!int.TryParse(rotateTag, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var angle))
                throw new PhotoContentInvalidException();
            var tagged = Normalize(angle);
            if (rotation is not null && rotation != tagged) throw new PhotoContentInvalidException();
            rotation = tagged;
        }

        return rotation ?? 0;
    }

    private static int RequiredRotation(JsonElement entry) => entry.TryGetProperty("rotation", out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var angle) && angle >= -360 && angle <= 360
        && Math.Abs(angle - decimal.Round(angle)) <= 0.01m
            ? (int)decimal.Round(angle) : throw new PhotoContentInvalidException();

    private static int Normalize(int angle) => angle % 90 == 0 ? (angle % 360 + 360) % 360
        : throw new PhotoContentInvalidException();

    private static void RequireOrthogonalMatrix(string? text, int angle)
    {
        var rows = text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rows is not { Length: 3 }) throw new PhotoContentInvalidException();

        Span<int> matrix = stackalloc int[9];
        for (var row = 0; row < 3; row++)
        {
            var colon = rows[row].IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) throw new PhotoContentInvalidException();
            var fields = rows[row][(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3) throw new PhotoContentInvalidException();

            for (var column = 0; column < fields.Length; column++)
            {
                if (!int.TryParse(fields[column], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                    out matrix[row * 3 + column])) throw new PhotoContentInvalidException();
            }
        }

        const int unit = 65_536;
        var (a, b, c, d) = angle switch
        {
            0 => (unit, 0, 0, unit),
            90 => (0, -unit, unit, 0),
            180 => (-unit, 0, 0, -unit),
            _ => (0, unit, -unit, 0)
        };
        if (!Near(matrix[0], a) || !Near(matrix[1], b) || !Near(matrix[3], c) || !Near(matrix[4], d)
            || matrix[2] != 0 || matrix[5] != 0 || matrix[8] != 1_073_741_824)
            throw new PhotoContentInvalidException();
    }

    private static bool Near(int value, int expected) => Math.Abs((long)value - expected) <= 16;
}
