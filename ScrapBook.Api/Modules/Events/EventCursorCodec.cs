using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ScrapBook.Application.Modules.Events;

namespace ScrapBook.Api.Modules.Events;

public sealed class EventCursorCodec(IDataProtectionProvider provider) : IEventCursorCodec
{
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("Events.List.v1").ToTimeLimitedDataProtector();
    private sealed record Payload(int V, string Order, long AccountId, DateTimeOffset CreatedAt, long EventId);

    public string Encode(EventCursor cursor) => protector.Protect(JsonSerializer.Serialize(
        new Payload(1, "created_desc_id_desc", cursor.AccountId, cursor.CreatedAt, cursor.EventId)), TimeSpan.FromHours(24));

    public EventCursor Decode(string token, long accountId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) throw InvalidCursor();
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(token));
            if (payload is null || payload.V != 1 || payload.Order != "created_desc_id_desc"
                || payload.AccountId != accountId || payload.EventId <= 0 || payload.CreatedAt.Offset != TimeSpan.Zero)
                throw InvalidCursor();
            return new(payload.AccountId, payload.CreatedAt, payload.EventId);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            throw InvalidCursor();
        }
    }

    private static EventFailure InvalidCursor() => new(EventFailureKind.Validation,
        new Dictionary<string, string[]> { ["cursor"] = ["Cursor inválido, expirado ou incompatível com a conta."] });
}
