using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ScrapBook.Api.Modules.Identity;

namespace ScrapBook.UnitTests.Modules.Identity;

public sealed class DevelopmentIdentityOptionsTests
{
    [Fact]
    public void SimulationIsDisabledByDefault() => Assert.False(new DevelopmentIdentityOptions().Enabled);

    [Fact]
    public void SimulationCannotStartOutsideDevelopment() => Assert.Throws<InvalidOperationException>(() =>
        new DevelopmentIdentityOptions { Enabled = true, AccountId = 42 }.Validate(new Host("Production"), Config()));

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://*:5080")]
    [InlineData("http://localhost.example:5080")]
    public void SimulationRequiresLoopbackBinding(string address) => Assert.Throws<InvalidOperationException>(() =>
        new DevelopmentIdentityOptions { Enabled = true, AccountId = 42 }.Validate(new Host("Development"), Config(address)));

    [Fact]
    public void SimulationRejectsWildcardOrigin() => Assert.Throws<InvalidOperationException>(() =>
        new DevelopmentIdentityOptions { Enabled = true, AccountId = 42, AllowedOrigins = ["*"] }
            .Validate(new Host("Development"), Config()));

    private static IConfiguration Config(string address = "http://127.0.0.1:5080") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ScrapBook"] = "Host=127.0.0.1;Username=synthetic",
            ["urls"] = address
        }).Build();

    private sealed class Host(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "UnitTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
