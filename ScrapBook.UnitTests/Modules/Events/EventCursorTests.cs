using Microsoft.AspNetCore.DataProtection;
using ScrapBook.Api.Modules.Events;
using ScrapBook.Application.Modules.Events;

namespace ScrapBook.UnitTests.Modules.Events;

public sealed class EventCursorTests
{
    private readonly IDataProtectionProvider provider = new EphemeralDataProtectionProvider();

    [Fact]
    public void RoundtripPreservesFullPrecisionAndAccountBinding()
    {
        var codec = new EventCursorCodec(provider);
        var cursor = new EventCursor(42, new DateTimeOffset(2026, 11, 12, 20, 0, 0, TimeSpan.Zero).AddTicks(1234560), long.MaxValue);
        var token = codec.Encode(cursor);
        Assert.Equal(cursor, codec.Decode(token, 42));
        Assert.Throws<EventFailure>(() => codec.Decode(token, 43));
        Assert.Throws<EventFailure>(() => codec.Decode(token[..^10] + "tampered", 42));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsExpiredOrIncompatiblePayload(int kind)
    {
        var protector = provider.CreateProtector("Events.List.v1").ToTimeLimitedDataProtector();
        var payload = "{\"V\":" + (kind == 1 ? "2" : "1") + ",\"Order\":\"created_desc_id_desc\",\"AccountId\":42,\"CreatedAt\":\"2026-11-12T20:00:00Z\",\"EventId\":1}";
        var token = protector.Protect(payload, DateTimeOffset.UtcNow.AddSeconds(kind == -1 ? -1 : 60));
        Assert.Throws<EventFailure>(() => new EventCursorCodec(provider).Decode(token, 42));
    }

    [Fact]
    public void RejectsOversizedToken() => Assert.Throws<EventFailure>(() => new EventCursorCodec(provider).Decode(new string('x', 2049), 42));
}
