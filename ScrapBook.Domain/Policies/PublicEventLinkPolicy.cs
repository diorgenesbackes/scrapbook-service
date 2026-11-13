using System.Text.RegularExpressions;

namespace ScrapBook.Domain.Policies;

public static class PublicEventLinkPolicy
{
    public static string? NormalizeOrigin(string? origin, bool allowHttpLoopback)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        var value = origin.Trim();
        if (value.Length > 512 || value.Any(char.IsControl)
            || !Regex.IsMatch(value, @"\Ahttps?://[^/?#\\]+/?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType == UriHostNameType.Unknown
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && allowHttpLoopback && uri.IsLoopback)))
            throw new ArgumentException("Configure uma origem HTTPS ou HTTP loopback local com opt-in explícito.", nameof(origin));
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static string? Build(string? origin, string code)
    {
        if (!PublicEventCodePolicy.IsValid(code)) throw new ArgumentException("Código público inválido.", nameof(code));
        return origin is null ? null : origin + "/e/" + code;
    }
}
