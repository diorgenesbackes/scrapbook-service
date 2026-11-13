using ScrapBook.Domain.Enums;
namespace ScrapBook.Application.Exceptions;

public sealed class EventApplicationException(EventFailureKindEnum kind, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(kind.ToString())
{
    public EventFailureKindEnum Kind { get; } = kind;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
}
