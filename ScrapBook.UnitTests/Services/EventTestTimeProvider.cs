namespace ScrapBook.UnitTests.Services;

internal sealed class EventTestTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2031-01-01T00:00:00Z");

    public override DateTimeOffset GetUtcNow() => Now;
}
