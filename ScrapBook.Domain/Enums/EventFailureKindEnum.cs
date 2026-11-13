namespace ScrapBook.Domain.Enums;

public enum EventFailureKindEnum
{
    Validation, AuthenticationRequired, OriginNotAllowed, NotFound, StructureLocked,
    OpeningLocked, VersionConflict, TooLarge, UnsupportedMediaType, DependencyUnavailable, Unexpected
}
