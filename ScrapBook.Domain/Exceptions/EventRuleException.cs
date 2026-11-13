using ScrapBook.Domain.Enums;
namespace ScrapBook.Domain.Exceptions;

public sealed class EventRuleException(EventRuleFailureEnum failure, string? field = null, string? safeMessage = null)
    : Exception(safeMessage ?? failure.ToString())
{
    public EventRuleFailureEnum Failure { get; } = failure;
    public string? Field { get; } = field;
}
