using ScrapBook.Application.Services;
using ScrapBook.Domain.Mappers;
using ScrapBook.Domain.Models;
using ScrapBook.Domain.Policies;
using ScrapBook.UnitTests.Stubs;

namespace ScrapBook.UnitTests.Services;

public sealed class PublicEventPresentationServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public async Task UsesTheSameDefaultAsEventManagement(string? input)
    {
        var model = new PublicEventModel(new string('A', 43), "Encontro", EventPolicy.NormalizeWelcome(input));
        var result = await new PublicEventService(new PublicEventRepositoryStub(model), new PublicSiteModel(null)).GetAsync(model.Code, default);
        Assert.Equal(EventPolicy.DefaultWelcomeMessage, result.EffectiveWelcomeMessage);
        Assert.Equal(EventPolicy.DefaultCoverAssetKey, result.CoverAssetKey);
    }

    [Fact]
    public async Task PreservesPersonalizedUnicodeAndLineBreaksAsData()
    {
        var name = EventPolicy.NormalizeName("Álbum 🎉 <evento>");
        var message = EventPolicy.NormalizeWelcome("Olá!\nCompartilhe <script>texto</script> & carinho 🎉");
        var model = new PublicEventModel(new string('A', 43), name, message);
        var result = await new PublicEventService(new PublicEventRepositoryStub(model), new PublicSiteModel(null)).GetAsync(model.Code, default);
        var response = PublicEventResponseMapper.ToResponse(result);
        Assert.Equal(name, response.Name);
        Assert.Equal(message, response.EffectiveWelcomeMessage);
        Assert.Equal("default", response.Cover.Kind);
        Assert.Equal(EventPolicy.DefaultCoverAssetKey, response.Cover.AssetKey);
    }

    [Fact]
    public async Task AcceptsExistingScalarLimitsWithoutTruncatingTheMessage()
    {
        var name = EventPolicy.NormalizeName(string.Concat(Enumerable.Repeat("🎉", 120)));
        var message = EventPolicy.NormalizeWelcome(string.Concat(Enumerable.Repeat("🎉", 2000)));
        var model = new PublicEventModel(new string('A', 43), name, message);
        var result = await new PublicEventService(new PublicEventRepositoryStub(model), new PublicSiteModel(null)).GetAsync(model.Code, default);
        Assert.Equal(name, result.Name);
        Assert.Equal(message, result.EffectiveWelcomeMessage);
    }
}
