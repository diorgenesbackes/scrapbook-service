namespace ScrapBook.Domain.Modules.Events;

public enum EventRuleFailure { Validation, StructureLocked, OpeningLocked }

public sealed class EventRuleException(EventRuleFailure failure, string? field = null, string? safeMessage = null)
    : Exception(safeMessage ?? failure.ToString())
{
    public EventRuleFailure Failure { get; } = failure;
    public string? Field { get; } = field;
}
