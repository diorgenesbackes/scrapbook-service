using ScrapBook.Application.Interfaces;
using ScrapBook.Domain.Models;

namespace ScrapBook.UnitTests.Stubs;

internal sealed class PublicEventRepositoryStub(PublicEventModel? model) : IPublicEventRepository
{
    public int Calls { get; private set; }
    public string? Code { get; private set; }
    public CancellationToken Token { get; private set; }

    public Task<PublicEventModel?> GetAsync(string code, CancellationToken cancellationToken)
    {
        Calls++;
        Code = code;
        Token = cancellationToken;
        return Task.FromResult(model?.Code == code ? model : null);
    }
}
