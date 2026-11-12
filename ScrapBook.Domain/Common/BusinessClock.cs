namespace ScrapBook.Domain.Common;

public sealed class BusinessClock(TimeProvider timeProvider)
{
    public const string TimeZoneId = "America/Sao_Paulo";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    public DateTime CivilNow => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(UtcNow, Zone).DateTime, DateTimeKind.Unspecified);
    public DateOnly Today => DateOnly.FromDateTime(CivilNow);
}
