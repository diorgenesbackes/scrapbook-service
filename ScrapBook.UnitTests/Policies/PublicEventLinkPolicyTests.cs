using ScrapBook.Domain.Policies;

namespace ScrapBook.UnitTests.Policies;

public sealed class PublicEventLinkPolicyTests
{
    [Theory]
    [InlineData("https://share.example.invalid", false, "https://share.example.invalid")]
    [InlineData("https://share.example.invalid/", false, "https://share.example.invalid")]
    [InlineData("http://127.0.0.1:5173", true, "http://127.0.0.1:5173")]
    [InlineData(null, false, null)]
    public void NormalizesOnlyConfiguredOrigins(string? input, bool allowLocal, string? expected) =>
        Assert.Equal(expected, PublicEventLinkPolicy.NormalizeOrigin(input, allowLocal));

    [Theory]
    [InlineData("http://public.example.invalid", true)]
    [InlineData("http://localhost:5173", false)]
    [InlineData("https://user:password@share.example.invalid", false)]
    [InlineData("https://share.example.invalid/path", false)]
    [InlineData("https://share.example.invalid/path/..", false)]
    [InlineData("https://share.example.invalid?redirect=evil", false)]
    [InlineData("https://share.example.invalid#fragment", false)]
    [InlineData("//share.example.invalid", false)]
    public void RejectsUntrustedOrNonOriginConfiguration(string input, bool allowLocal) =>
        Assert.Throws<ArgumentException>(() => PublicEventLinkPolicy.NormalizeOrigin(input, allowLocal));

    [Fact]
    public void ReusesTheExactPublicCode()
    {
        var code = "A" + new string('a', 41) + "A";
        Assert.Equal("https://share.example.invalid/e/" + code, PublicEventLinkPolicy.Build("https://share.example.invalid", code));
        Assert.Null(PublicEventLinkPolicy.Build(null, code));
        Assert.Throws<ArgumentException>(() => PublicEventLinkPolicy.Build("https://share.example.invalid", "1"));
    }
}
