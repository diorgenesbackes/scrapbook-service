using Microsoft.Extensions.Configuration;
using ScrapBook.Api.Options;

namespace ScrapBook.UnitTests.Options;

public sealed class StartupOptionsTests
{
    [Fact]
    public void IsDisabledByDefault()
    {
        var identity = DevelopmentIdentityOptions.FromConfiguration(new ConfigurationBuilder().Build(), false);
        Assert.False(identity.Enabled);
    }

    [Fact]
    public void RejectsEnabledIdentityOutsideDevelopment()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DevelopmentIdentity:Enabled"] = "true"
        }).Build();
        Assert.Equal("DevelopmentIdentity:Enabled",
            Assert.Throws<StartupConfigurationException>(() => DevelopmentIdentityOptions.FromConfiguration(settings, false)).OptionName);
    }

    [Theory]
    [InlineData("true", "2", true)]
    [InlineData("false", "invalid", false)]
    public void RequiresExplicitDevelopmentIdentity(string enabled, string accountId, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DevelopmentIdentity:Enabled"] = enabled,
            ["DevelopmentIdentity:AccountId"] = accountId,
            ["DevelopmentIdentity:Token"] = new string('a', 64)
        }).Build();
        var identity = DevelopmentIdentityOptions.FromConfiguration(config, true);
        Assert.Equal(expected, identity.Enabled);
        Assert.Equal(expected ? 2 : 0, identity.AccountId);
    }

    [Theory]
    [InlineData("Host=localhost;Database=scrapbook;Username=postgres;Password=local-fixture-secret", "2000", "ConnectionStrings:ScrapBook")]
    [InlineData("Host=localhost;Database=scrapbook;Username=scrapbook_app;Password=local-fixture-secret", "invalid", "Database:ReadinessTimeoutMs")]
    [InlineData("invalid=local-fixture-secret", "2000", "ConnectionStrings:ScrapBook")]
    [InlineData(null, "2000", "ConnectionStrings:ScrapBook")]
    public void InvalidConfigurationNamesOnlyTheOption(string? connection, string timeout, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ScrapBook"] = connection,
            ["Database:ReadinessTimeoutMs"] = timeout
        }).Build();
        var error = Assert.Throws<StartupConfigurationException>(() => DatabaseOptions.FromConfiguration(config));
        Assert.Equal(expected, error.OptionName);
        Assert.DoesNotContain("local-fixture-secret", error.ToString());
    }
}
