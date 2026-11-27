namespace ScrapBook.Domain.Exceptions;

public sealed class MediaPreparationWriteException : Exception
{
    public MediaPreparationWriteException() : base("The private media preparation write could not be completed.")
    {
    }
}
