using ScrapBook.Api.Models;
using ScrapBook.Application.Enums;
using ScrapBook.Application.Exceptions;
using ScrapBook.Application.Interfaces;
using ScrapBook.Application.Models;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ScrapBook.Api.Services;

public sealed class EventCursorService(IDataProtectionProvider provider) : IEventCursorService
{
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("Events.List.v1").ToTimeLimitedDataProtector();

    public string Encode(EventCursorModel cursor) => protector.Protect(JsonSerializer.Serialize(
        new EventCursorPayloadModel(1, "created_desc_id_desc", cursor.AccountId, cursor.CreatedAt, cursor.EventId)), TimeSpan.FromHours(24));

    public EventCursorModel Decode(string token, long accountId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) throw InvalidCursor();
        try
        {
            var payload = JsonSerializer.Deserialize<EventCursorPayloadModel>(protector.Unprotect(token));
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

    private static EventApplicationException InvalidCursor() => new(EventFailureKindEnum.Validation,
        new Dictionary<string, string[]> { ["cursor"] = ["Cursor inválido, expirado ou incompatível com a conta."] });
}
