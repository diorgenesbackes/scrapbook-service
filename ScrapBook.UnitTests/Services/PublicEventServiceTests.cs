using ScrapBook.Application.Services;
using ScrapBook.Domain.Exceptions;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Services;

public sealed class PublicEventServiceTests
{
    private const string Code = "_lv5Uj97Qkq7kckeOtjXoY3jHviZqegNrKxNPEp8yXQ";
    private readonly EventTestRepository repository = new();
    private readonly EventTestTimeProvider clock = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("!lv5Uj97Qkq7kckeOtjXoY3jHviZqegNrKxNPEp8yXQ")]
    public async Task RejectsMalformedCodeWithoutQueryAsync(string? code)
    {
        await Assert.ThrowsAsync<PublicEventNotFoundException>(() => Service().GetAsync(code, CancellationToken.None));
        Assert.Null(repository.SearchedCode);
    }

    [Fact]
    public async Task RejectsAbsentCodeAsync()
    {
        await Assert.ThrowsAsync<PublicEventNotFoundException>(() => Service().GetAsync(Code, CancellationToken.None));
        Assert.Equal(Code, repository.SearchedCode);
    }

    [Fact]
    public async Task ReadsCurrentPresentationAndPropagatesCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var service = Service();
        var item = new PublicEventModel("Festa", null, new DateTime(2031, 1, 1, 1, 0, 0),
            new DateTime(2030, 12, 31, 23, 0, 0), new DateTime(2031, 1, 2, 12, 0, 0), new DateTime(2031, 1, 2, 13, 0, 0));
        repository.PublicEvent = item;

        var initial = await service.GetAsync(Code, cancellation.Token);
        Assert.Equal("Festa", initial.Name);
        Assert.Equal("default", initial.Cover.Kind);
        Assert.Equal("Seja bem-vindo! Compartilhe uma lembrança deste evento.", initial.WelcomeMessage);
        Assert.Equal("America/Sao_Paulo", initial.TimeZone);
        Assert.Equal(clock.Now, initial.ServerTime);
        Assert.Equal("before", initial.SubmissionWindow.State);

        repository.PublicEvent = item with { Name = "Novo nome", WelcomeMessage = "Mensagem atual" };
        var edited = await service.GetAsync(Code, cancellation.Token);
        Assert.Equal("Novo nome", edited.Name);
        Assert.Equal("Mensagem atual", edited.WelcomeMessage);
        Assert.Equal(cancellation.Token, repository.PublicLookupToken);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(Code, cancellation.Token));
    }

    private PublicEventService Service() => new(repository, clock);
}
