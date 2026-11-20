using System.Xml.Linq;

namespace ScrapBook.UnitTests.Architecture;

public sealed class ProjectReferencesTests
{
    [Theory]
    [InlineData("Api", "Application,Domain,Infrastructure")]
    [InlineData("Application", "Domain")]
    [InlineData("Infrastructure", "Domain")]
    [InlineData("Domain", "")]
    public void ReferencesMatchArchitecture(string project, string references)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "ScrapBook.sln")))
        {
            root = root.Parent ?? throw new DirectoryNotFoundException("ScrapBook.sln");
        }

        var name = $"ScrapBook.{project}";
        var xml = XDocument.Load(Path.Combine(root.FullName, name, $"{name}.csproj"));
        var actual = xml.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value)
                .Replace("ScrapBook.", "", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        Assert.Equal(references.Split(',', StringSplitOptions.RemoveEmptyEntries), actual);
        if (project == "Domain")
        {
            Assert.Empty(xml.Descendants("PackageReference"));
            Assert.Empty(xml.Descendants("FrameworkReference"));
        }
    }
}
