namespace ClaudeCodeProxy;

// Computes which "stats day" an instant belongs to under a configurable day boundary.
// A boundary of 20:00 in the given time zone means the period from 20:00 on day D to
// 20:00 on day D+1 is labeled D+1 — the label is the day the period ends on.
internal static class StatsDay
{
    public static DateOnly For(DateTimeOffset instant, TimeZoneInfo timeZone, TimeSpan boundary)
    {
        var zoned = TimeZoneInfo.ConvertTime(instant, timeZone);
        var date = DateOnly.FromDateTime(zoned.DateTime);
        if (boundary == TimeSpan.Zero) return date;
        return zoned.TimeOfDay >= boundary ? date.AddDays(1) : date;
    }
}
