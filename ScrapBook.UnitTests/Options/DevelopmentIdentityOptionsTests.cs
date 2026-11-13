using ScrapBook.Api.Options;
using ScrapBook.UnitTests.Stubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ScrapBook.UnitTests.Options;

public sealed class DevelopmentIdentityOptionsTests
{
    [Fact]
    public void SimulationIsDisabledByDefault() => Assert.False(new DevelopmentIdentityOptions().Enabled);

    [Fact]
    public void SimulationCannotStartOutsideDevelopment() => Assert.Throws<InvalidOperationException>(() =>
        new DevelopmentIdentityOptions { Enabled = true, AccountId = 42 }.Validate(new HostEnvironmentStub("Production"), Config()));

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://*:5080")]
    [InlineData("http://localhost.example:5080")]
    public void SimulationRequiresLoopbackBinding(string address) => Assert.Throws<InvalidOperationException>(() =>
        new DevelopmentIdentityOptions { Enabled = true, AccountId = 42 }.Validate(new HostEnvironmentStub("Development"), Config(address)));

    [Fact]
    public void SimulationRejectsWildcardOrigin() => Assert.Throws<InvalidOperationException>(() =>
        new DevelopmentIdentityOptions { Enabled = true, AccountId = 42, AllowedOrigins = ["*"] }
            .Validate(new HostEnvironmentStub("Development"), Config()));

    [Theory]
    [InlineData(true, false, "Development", false)]
    [InlineData(false, true, "Development", false)]
    [InlineData(true, true, "Production", false)]
    [InlineData(true, true, "Development", true)]
    public void ContainerBindingRequiresExplicitOptInAndDevelopment(bool optIn, bool inContainer, string environment, bool valid)
    {
        var options = new DevelopmentIdentityOptions { Enabled = true, AccountId = 42, AllowContainerBinding = optIn };
        var config = Config("http://0.0.0.0:8080");
        config["DOTNET_RUNNING_IN_CONTAINER"] = inContainer.ToString();
        var error = Record.Exception(() => options.Validate(new HostEnvironmentStub(environment), config));
        if (valid) Assert.Null(error);
        else Assert.IsType<InvalidOperationException>(error);
    }

    private static IConfiguration Config(string address = "http://127.0.0.1:5080") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ScrapBook"] = "Host=127.0.0.1;Username=synthetic",
            ["urls"] = address
        }).Build();

}
