using System.Reflection;
using ScrapBook.Api.Controllers;
using ScrapBook.Application.Services;
using ScrapBook.Domain.Entities;
using ScrapBook.Infrastructure.DbContexts;

namespace ScrapBook.UnitTests.Architecture;

public sealed class LayerDependencyTests
{
    [Fact]
    public void DomainAndApplicationReferenceOnlyBaseLibrariesAndDomain()
    {
        foreach (var assembly in new[] { typeof(EventEntity).Assembly, typeof(EventService).Assembly })
        {
            var forbidden = assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name is not "ScrapBook.Domain"
                    && reference.Name?.StartsWith("System.", StringComparison.Ordinal) != true);
            Assert.Empty(forbidden);
        }
    }

    [Fact]
    public void ServiceImplementationsBelongToApplication()
    {
        var application = typeof(EventService).Assembly;
        Assembly[] assemblies = [application, typeof(EventEntity).Assembly,
            typeof(EventController).Assembly, typeof(ScrapBookDbContext).Assembly];
        var services = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && type.Name.EndsWith("Service", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(services);
        Assert.All(services, service => Assert.Equal(application, service.Assembly));
    }
}
