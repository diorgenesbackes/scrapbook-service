using ScrapBook.Application.Models;
namespace ScrapBook.Application.Interfaces;

public interface IEventCursorService
{
    string Encode(EventCursorModel cursor);
    EventCursorModel Decode(string token, long accountId);
}
