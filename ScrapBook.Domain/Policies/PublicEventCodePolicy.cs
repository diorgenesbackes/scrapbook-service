namespace ScrapBook.Domain.Policies;

public static class PublicEventCodePolicy
{
    public static bool IsValid(string? code)
    {
        if (code is not { Length: 43 } || code.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) return false;
        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(code.Replace('-', '+').Replace('_', '/') + "=", bytes, out var written)
            && written == 32
            && Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == code;
    }
}
