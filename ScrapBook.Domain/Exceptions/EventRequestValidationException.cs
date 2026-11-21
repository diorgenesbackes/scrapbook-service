namespace ScrapBook.Domain.Exceptions;

public sealed class EventRequestValidationException(string field) : Exception
{
    public string Field { get; } = field;
}
