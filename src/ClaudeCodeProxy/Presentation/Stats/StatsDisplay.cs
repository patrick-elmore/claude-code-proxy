using System.Globalization;

namespace ClaudeCodeProxy.Presentation.Stats;

public static class StatsDisplay
{
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(double value) => value.ToString("F0", CultureInfo.InvariantCulture) + "%";

    public static string Milliseconds(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    public static string HitClass(double value) => value >= 70 ? "hit" : value >= 30 ? "warn" : "";

    public static string Money(decimal usd)
    {
        if (usd == 0m) return "$0.00";
        if (usd < 0.01m) return "$" + usd.ToString("F4", CultureInfo.InvariantCulture);
        return "$" + usd.ToString("N2", CultureInfo.InvariantCulture);
    }

    public static string Uptime(double seconds)
    {
        var value = TimeSpan.FromSeconds(seconds);
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}d {value.Hours}h";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1) return $"{(int)value.TotalMinutes}m {value.Seconds}s";
        return $"{(int)value.TotalSeconds}s";
    }
}
