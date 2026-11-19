using Microsoft.Extensions.Configuration;
using ScrapBook.Api.Options;

namespace ScrapBook.UnitTests.Options;

public sealed class StartupOptionsTests
{
    [Fact]
    public void IsDisabledByDefault()
    {
        var identity = DevelopmentIdentityOptions.FromConfiguration(new ConfigurationBuilder().Build(), false, false);
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
            Assert.Throws<StartupConfigurationException>(() => DevelopmentIdentityOptions.FromConfiguration(settings, false, true)).OptionName);
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
        var identity = DevelopmentIdentityOptions.FromConfiguration(config, true, false);
        Assert.Equal(expected, identity.Enabled);
        Assert.Equal(expected ? 2 : 0, identity.AccountId);
    }

    [Theory]
    [InlineData("urls", "http://localhost:5080;http://[::1]:5081", false, null, true)]
    [InlineData("urls", "http://0.0.0.0:5080", false, "true", false)]
    [InlineData("urls", "http://127.0.0.1:5080;http://+:5081", false, null, false)]
    [InlineData("urls", "http://fixture@localhost:5080", false, null, false)]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://127.0.0.1:5080", false, null, true)]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:5080", true, null, false)]
    [InlineData("HTTP_PORTS", "8080", true, "true", true)]
    [InlineData("HTTP_PORTS", "8080", true, "false", false)]
    [InlineData("HTTP_PORTS", "8080", true, "invalid", false)]
    [InlineData("HTTPS_PORTS", "443", false, "true", false)]
    public void ValidatesEffectiveBindingForDevelopmentIdentity(string bindingKey, string binding, bool isContainer,
        string? optIn, bool allowed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [bindingKey] = binding,
            ["DevelopmentIdentity:Enabled"] = "true",
            ["DevelopmentIdentity:AllowContainerBinding"] = optIn,
            ["DevelopmentIdentity:AccountId"] = "42",
            ["DevelopmentIdentity:Token"] = new string('a', 64)
        }).Build();

        if (allowed)
        {
            Assert.True(DevelopmentIdentityOptions.FromConfiguration(config, true, isContainer).Enabled);
        }
        else
        {
            var error = Assert.Throws<StartupConfigurationException>(
                () => DevelopmentIdentityOptions.FromConfiguration(config, true, isContainer));
            Assert.Equal("DevelopmentIdentity:AllowContainerBinding", error.OptionName);
        }
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
