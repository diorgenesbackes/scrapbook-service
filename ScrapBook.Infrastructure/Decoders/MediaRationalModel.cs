using System.Globalization;
using System.Numerics;
using ScrapBook.Domain.Exceptions;

namespace ScrapBook.Infrastructure.Decoders;

internal readonly record struct MediaRationalModel(int Numerator, int Denominator)
{
    public decimal Value => (decimal)Numerator / Denominator;

    public static MediaRationalModel? Parse(string? text, char separator)
    {
        var value = text.AsSpan();
        var split = value.IndexOf(separator);
        if (split <= 0 || !int.TryParse(value[..split], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
            || numerator <= 0 || !int.TryParse(value[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture,
                out var denominator) || denominator <= 0) return null;
        return Reduce(numerator, denominator);
    }

    public static MediaRationalModel Reduce(BigInteger numerator, BigInteger denominator)
    {
        if (numerator <= 0 || denominator <= 0) throw new PhotoContentInvalidException();

        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
        if (numerator <= int.MaxValue && denominator <= int.MaxValue)
            return new MediaRationalModel((int)numerator, (int)denominator);

        const int precision = 1_000_000;
        var scaled = numerator * precision / denominator;
        if (scaled <= 0 || scaled > int.MaxValue) throw new PhotoContentInvalidException();
        return Reduce(scaled, precision);
    }
}
