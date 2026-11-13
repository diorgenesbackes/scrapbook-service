namespace ScrapBook.Application.Interfaces;

public interface IEventCursorProtector
{
    string Protect(string payload, TimeSpan lifetime);
    string Unprotect(string token);
}
