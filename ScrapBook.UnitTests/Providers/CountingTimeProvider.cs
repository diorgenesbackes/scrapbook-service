namespace ScrapBook.UnitTests.Providers;

public sealed class CountingTimeProvider(DateTimeOffset instant) : TimeProvider
{
    public int Calls { get; private set; }
    public override DateTimeOffset GetUtcNow()
    {
        Calls++;
        return instant;
    }
}
