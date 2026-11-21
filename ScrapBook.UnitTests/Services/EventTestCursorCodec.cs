using ScrapBook.Domain.Interfaces;

namespace ScrapBook.UnitTests.Services;

internal sealed class EventTestCursorCodec : IEventCursorCodec
{
    public string Encode(long accountId, long beforeId) => $"{accountId}:{beforeId}";

    public long? Decode(string cursor, long accountId) =>
        cursor == Encode(accountId, 11) ? 11 : null;
}
