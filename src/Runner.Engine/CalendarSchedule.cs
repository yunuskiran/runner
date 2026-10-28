using Cronos;
namespace Runner.Engine;

public static class CalendarSchedule
{
    public static DateTime Next(Schedule schedule, DateTime after)
    {
        if (schedule.Cron is null) return after.AddSeconds(schedule.IntervalSeconds);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        return CronExpression.Parse(schedule.Cron, CronFormat.Standard).GetNextOccurrence(DateTime.SpecifyKind(after, DateTimeKind.Utc), zone)
            ?? throw new ArgumentException("Cron expression has no future occurrence");
    }

    public static void Validate(Schedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.TimeZoneId)) throw new ArgumentException("Time zone required");
        if (schedule.MisfirePolicy is not ("Coalesce" or "Skip" or "CatchUp")) throw new ArgumentException("Invalid misfire policy");
        if (schedule.Cron is null && schedule.IntervalSeconds is < 10 or > 31536000) throw new ArgumentException("Invalid interval");
        if (schedule.Cron?.Length > 120 || schedule.TimeZoneId.Length > 120) throw new ArgumentException("Invalid calendar schedule");
        _ = Next(schedule, DateTime.UtcNow);
    }
}
