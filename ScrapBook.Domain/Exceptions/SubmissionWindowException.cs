namespace ScrapBook.Domain.Exceptions;

public sealed class SubmissionWindowException(string windowState) : Exception
{
    public string WindowState { get; } = windowState;
}
