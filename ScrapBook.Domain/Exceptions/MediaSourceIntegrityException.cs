namespace ScrapBook.Domain.Exceptions;

public sealed class MediaSourceIntegrityException : Exception
{
    public MediaSourceIntegrityException() : base("The confirmed private media source failed verification.")
    {
    }
}
