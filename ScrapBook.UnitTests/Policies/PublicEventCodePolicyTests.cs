using ScrapBook.Domain.Policies;

namespace ScrapBook.UnitTests.Policies;

public sealed class PublicEventCodePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("../events")]
    public void RejectsMissingAndMalformedCode(string? code) => Assert.False(PublicEventCodePolicy.IsValid(code));

    [Fact]
    public void AcceptsCodesProducedByExistingEventCreation()
    {
        for (var index = 0; index < 100; index++) Assert.True(PublicEventCodePolicy.IsValid(EventPolicy.CreateCode()));
    }

    [Fact]
    public void RejectsPaddingWhitespaceAndWrongLength()
    {
        var code = new string('A', 43);
        foreach (var invalid in new[] { code + "=", " " + code, code + " ", code[..42], code + "A", code[..42] + "/" })
            Assert.False(PublicEventCodePolicy.IsValid(invalid));
    }

    [Fact]
    public void RejectsNonCanonicalTrailingBits() => Assert.False(PublicEventCodePolicy.IsValid(new string('A', 42) + "B"));

    [Fact]
    public void PreservesCaseAsPartOfTheIdentifier()
    {
        Assert.True(PublicEventCodePolicy.IsValid("A" + new string('a', 41) + "A"));
        Assert.True(PublicEventCodePolicy.IsValid("a" + new string('a', 41) + "A"));
    }
}
