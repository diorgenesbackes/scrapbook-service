namespace ScrapBook.Application.Modules.Events;

public enum EventFailureKind
{
    Validation, AuthenticationRequired, OriginNotAllowed, NotFound, StructureLocked,
    OpeningLocked, VersionConflict, TooLarge, UnsupportedMediaType, DependencyUnavailable, Unexpected
}

public sealed class EventFailure(EventFailureKind kind, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(kind.ToString())
{
    public EventFailureKind Kind { get; } = kind;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
}

public interface IEventUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
