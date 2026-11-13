namespace ScrapBook.Domain.Clocks;

public sealed class BusinessClock(TimeProvider timeProvider)
{
    public const string TimeZoneId = "America/Sao_Paulo";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    public static DateTime ToCivil(DateTimeOffset instant) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, Zone).DateTime, DateTimeKind.Unspecified);
    public DateTime CivilNow => ToCivil(UtcNow);
    public DateOnly Today => DateOnly.FromDateTime(CivilNow);
}
