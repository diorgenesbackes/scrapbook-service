namespace ScrapBook.Domain.Interfaces;

public interface IEventCursorCodec
{
    string Encode(long accountId, long beforeId);
    long? Decode(string cursor, long accountId);
}
