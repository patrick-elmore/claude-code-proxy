using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace ClaudeCodeProxy;

internal static class StatsHtmlRenderer
{
    public static string Render(StatsSnapshot s)
    {
        var totalIn = s.Totals.InputUncached + s.Totals.CacheRead + s.Totals.CacheCreation;
        var sb = new StringBuilder(8192);

        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <title>Claude Code Proxy — {{Esc(s.Date)}}</title>
            <style>
            html { background: #1a1a1a; }
            body { font-family: system-ui, -apple-system, sans-serif; max-width: 900px; margin: 1.5rem auto; padding: 0 1rem; color: #e0e0e0; background: #1a1a1a; }
            h1 { font-size: 1.4rem; margin: 0 0 .25rem; color: #fff; }
            h2 { font-size: 1.05rem; margin: 1.75rem 0 .25rem; border-bottom: 1px solid #333; padding-bottom: .25rem; color: #fff; }
            table { border-collapse: collapse; width: 100%; margin-top: .25rem; }
            th, td { text-align: left; padding: .3rem .55rem; border-bottom: 1px solid #2a2a2a; font-variant-numeric: tabular-nums; }
            th { background: #262626; font-weight: 600; color: #ddd; }
            td.num, th.num { text-align: right; }
            .muted { color: #888; font-size: .9rem; }
            .pair { margin: .25rem 0; }
            .pair .k { color: #999; margin-right: .5rem; }
            .pair .v { font-weight: 600; color: #fff; }
            .hit { color: #4caf82; }
            .warn { color: #d68a04; }
            footer { margin-top: 2rem; font-size: .85rem; color: #777; }
            footer a { color: #9ab; text-decoration: none; }
            footer a:hover { text-decoration: underline; }
            </style>
            </head><body>
            """);

        sb.Append($"<h1>Claude Code Proxy</h1>");
        sb.Append($"<p class=\"muted\">stats for {Esc(s.Date)} &middot; uptime: {Esc(FormatUptime(s.UptimeSeconds))}</p>");

        sb.Append("<h2>Today</h2>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests</span><span class=\"v\">{N0(s.RequestCount)}</span></div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">cache hit rate</span><span class=\"v {HitClass(s.HitRatePct)}\">{s.HitRatePct:F0}%</span> <span class=\"muted\">on {N0(totalIn)} input tokens</span></div>");

        sb.Append("<h2>Token totals</h2>");
        sb.Append("<table><tr><th>type</th><th class=\"num\">tokens</th></tr>");
        sb.Append($"<tr><td class=\"hit\">cache read</td><td class=\"num\">{N0(s.Totals.CacheRead)}</td></tr>");
        sb.Append($"<tr><td class=\"warn\">cache creation</td><td class=\"num\">{N0(s.Totals.CacheCreation)}</td></tr>");
        sb.Append($"<tr><td>uncached input</td><td class=\"num\">{N0(s.Totals.InputUncached)}</td></tr>");
        sb.Append($"<tr><td>output</td><td class=\"num\">{N0(s.Totals.Output)}</td></tr>");
        sb.Append("</table>");

        if (s.Hourly.Count > 0)
        {
            sb.Append("<h2>Hourly</h2>");
            sb.Append("<table><tr><th>hour</th><th class=\"num\">requests</th><th class=\"num\">hit %</th><th class=\"num\">cache read</th><th class=\"num\">cache create</th><th class=\"num\">uncached</th><th class=\"num\">output</th></tr>");
            foreach (var h in s.Hourly)
            {
                var hourLabel = h.Hour.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                sb.Append($"<tr><td>{Esc(hourLabel)}</td>")
                  .Append($"<td class=\"num\">{N0(h.Requests)}</td>")
                  .Append($"<td class=\"num {HitClass(h.HitRatePct)}\">{h.HitRatePct:F0}%</td>")
                  .Append($"<td class=\"num\">{N0(h.CacheRead)}</td>")
                  .Append($"<td class=\"num\">{N0(h.CacheCreation)}</td>")
                  .Append($"<td class=\"num\">{N0(h.InputUncached)}</td>")
                  .Append($"<td class=\"num\">{N0(h.Output)}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("<footer><a href=\"/stats.json\">raw JSON</a></footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Esc(string? s) => HtmlEncoder.Default.Encode(s ?? "");
    private static string N0(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string HitClass(double pct) => pct >= 70 ? "hit" : pct >= 30 ? "warn" : "";

    private static string FormatUptime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalSeconds}s";
    }
}
