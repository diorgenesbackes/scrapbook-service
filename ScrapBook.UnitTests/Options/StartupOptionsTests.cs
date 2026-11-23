using Microsoft.Extensions.Configuration;
using Npgsql;
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

    [Theory]
    [InlineData("", 32)]
    [InlineData(";Maximum Pool Size=48", 48)]
    [InlineData(";Minimum Pool Size=36", 36)]
    [InlineData(";Pooling=false", 100)]
    public void UsesBoundedPoolByDefaultAndRespectsExplicitConfiguration(string parameters, int maxPoolSize)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ScrapBook"] = "Host=localhost;Database=scrapbook;Username=scrapbook_app;Password=fixture" + parameters,
        }).Build();

        var settings = new NpgsqlConnectionStringBuilder(DatabaseOptions.FromConfiguration(config).ConnectionString);
        Assert.Equal(maxPoolSize, settings.MaxPoolSize);
    }

    [Theory]
    [InlineData("VerifyFull", "/app/certs/rds-us-east-1.pem", "true", null)]
    [InlineData("Require", "/app/certs/rds-us-east-1.pem", "true", "ConnectionStrings:ScrapBook")]
    [InlineData("VerifyFull", "/tmp/fixture.pem", "true", "ConnectionStrings:ScrapBook")]
    [InlineData("Require", "/tmp/fixture.pem", "false", null)]
    [InlineData("VerifyFull", "/app/certs/rds-us-east-1.pem", "invalid", "Database:RequireTls")]
    public void ValidatesRdsTlsWhenRequired(string sslMode, string certificate, string required, string? errorOption)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ScrapBook"] = "Host=db.example.test;Database=scrapbook;Username=scrapbook_app;"
                + $"Password=fixture-secret;SSL Mode={sslMode};Root Certificate={certificate}",
            ["Database:RequireTls"] = required
        }).Build();
        if (errorOption is null)
        {
            Assert.NotEmpty(DatabaseOptions.FromConfiguration(config).ConnectionString);
        }
        else
        {
            var error = Assert.Throws<StartupConfigurationException>(() => DatabaseOptions.FromConfiguration(config));
            Assert.Equal(errorOption, error.OptionName);
            Assert.DoesNotContain("fixture-secret", error.ToString());
        }
    }

    [Fact]
    public void LeavesPublicOriginDisabledByDefault()
    {
        Assert.Null(PublicUiOriginOptions.FromConfiguration(new ConfigurationBuilder().Build()).Origin);
    }

    [Theory]
    [InlineData("https://ui.hlg.example.test", true)]
    [InlineData("http://ui.hlg.example.test", false)]
    [InlineData("https://ui.hlg.example.test:444", false)]
    [InlineData("https://ui.hlg.example.test/", false)]
    [InlineData("https://fixture@ui.hlg.example.test", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("invalid", false)]
    public void AcceptsOnlyAnExplicitHttpsOrigin(string value, bool accepted)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublicUiOriginOptions.ConfigurationKey] = value
        }).Build();
        if (accepted)
        {
            Assert.Equal(value, PublicUiOriginOptions.FromConfiguration(config).Origin);
        }
        else
        {
            Assert.Equal(PublicUiOriginOptions.ConfigurationKey, Assert.Throws<StartupConfigurationException>(
                () => PublicUiOriginOptions.FromConfiguration(config)).OptionName);
        }
    }
}
