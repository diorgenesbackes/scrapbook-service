namespace ScrapBook.Domain.Exceptions;

public sealed class MediaOutputInvalidException : Exception
{
    public MediaOutputInvalidException() : base("The prepared private photo variant is invalid.")
    {
    }
}
