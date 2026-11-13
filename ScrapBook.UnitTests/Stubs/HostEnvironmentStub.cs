using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ScrapBook.UnitTests.Stubs;

internal sealed class HostEnvironmentStub(string environment) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environment;
    public string ApplicationName { get; set; } = "UnitTests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
