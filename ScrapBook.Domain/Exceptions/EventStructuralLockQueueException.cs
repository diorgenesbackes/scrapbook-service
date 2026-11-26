namespace ScrapBook.Domain.Exceptions;

public sealed class EventStructuralLockQueueException(Exception innerException)
    : Exception("Falha na fila de travamento.", innerException);
