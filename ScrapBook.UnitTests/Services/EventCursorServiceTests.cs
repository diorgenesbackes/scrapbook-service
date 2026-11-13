using ScrapBook.Api.DataProtectors;
using ScrapBook.Application.Services;
using ScrapBook.Application.Exceptions;
using ScrapBook.Domain.Models;
using Microsoft.AspNetCore.DataProtection;

namespace ScrapBook.UnitTests.Services;

public sealed class EventCursorServiceTests
{
    private readonly IDataProtectionProvider provider = new EphemeralDataProtectionProvider();

    [Fact]
    public void RoundtripPreservesFullPrecisionAndAccountBinding()
    {
        var codec = new EventCursorService(new EventCursorDataProtector(provider));
        var cursor = new EventCursorModel(42, new DateTimeOffset(2026, 11, 12, 20, 0, 0, TimeSpan.Zero).AddTicks(1234560), long.MaxValue);
        var token = codec.Encode(cursor);
        Assert.Equal(cursor, codec.Decode(token, 42));
        Assert.Throws<EventApplicationException>(() => codec.Decode(token, 43));
        Assert.Throws<EventApplicationException>(() => codec.Decode(token[..^10] + "tampered", 42));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsExpiredOrIncompatiblePayload(int kind)
    {
        var protector = provider.CreateProtector("Events.List.v1").ToTimeLimitedDataProtector();
        var payload = "{\"V\":" + (kind == 1 ? "2" : "1") + ",\"Order\":\"created_desc_id_desc\",\"AccountId\":42,\"CreatedAt\":\"2026-11-12T20:00:00Z\",\"EventId\":1}";
        var token = protector.Protect(payload, DateTimeOffset.UtcNow.AddSeconds(kind == -1 ? -1 : 60));
        Assert.Throws<EventApplicationException>(() => new EventCursorService(new EventCursorDataProtector(provider)).Decode(token, 42));
    }

    [Fact]
    public void RejectsOversizedToken() => Assert.Throws<EventApplicationException>(() => new EventCursorService(new EventCursorDataProtector(provider)).Decode(new string('x', 2049), 42));
}
