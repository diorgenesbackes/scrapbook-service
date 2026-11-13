namespace ScrapBook.UnitTests.Providers;

public sealed class ManualTimeProvider(DateTimeOffset instant) : TimeProvider
{
    private DateTimeOffset current = instant.ToUniversalTime();
    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        current += duration;
    }
}
