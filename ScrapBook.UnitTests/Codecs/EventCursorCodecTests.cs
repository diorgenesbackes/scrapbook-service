using Microsoft.AspNetCore.DataProtection;
using ScrapBook.Api.Codecs;

namespace ScrapBook.UnitTests.Codecs;

public sealed class EventCursorCodecTests
{
    [Fact]
    public void CursorCanOnlyBeReadForTheOriginAccount()
    {
        var codec = new EventCursorCodec(new EphemeralDataProtectionProvider());
        var cursor = codec.Encode(5, 42);
        Assert.Equal(42, codec.Decode(cursor, 5));
        Assert.Null(codec.Decode(cursor, 6));
        Assert.Null(codec.Decode("!", 5));
        Assert.Null(codec.Decode(new string('a', 513), 5));
        Assert.Null(codec.Decode(codec.Encode(5, -1), 5));
    }
}
