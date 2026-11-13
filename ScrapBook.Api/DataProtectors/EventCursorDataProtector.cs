using Microsoft.AspNetCore.DataProtection;
using ScrapBook.Application.Interfaces;

namespace ScrapBook.Api.DataProtectors;

public sealed class EventCursorDataProtector(IDataProtectionProvider provider) : IEventCursorProtector
{
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("Events.List.v1").ToTimeLimitedDataProtector();

    public string Protect(string payload, TimeSpan lifetime) => protector.Protect(payload, lifetime);
    public string Unprotect(string token) => protector.Unprotect(token);
}
