using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace ClaudeCodeProxy;

internal static class StatsHtmlRenderer
{
    public static string Render(StatsSnapshot s, PricingCatalog pricing,
        DateOnly? prevDate = null, DateOnly? nextDate = null)
    {
        var totalIn = s.Totals.InputUncached + s.Totals.CacheRead + s.Totals.CacheCreation;

        var perModelCost = new Dictionary<string, decimal>(s.ByModel.Count);
        decimal dayTotalCost = 0m;
        foreach (var (model, m) in s.ByModel)
        {
            var cost = pricing.CalculateCostUsd(model, m.InputUncached, m.CacheRead, m.CacheCreation5m, m.CacheCreation1h, m.Output);
            perModelCost[model] = cost;
            dayTotalCost += cost;
        }

        // Hourly buckets don't carry per-model data, so per-hour cost uses a blended rate from today's by-model mix.
        var blended = ComputeBlendedRates(s, pricing);

        var sb = new StringBuilder(8192);

        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><link rel="icon" href="/favicon.svg" type="image/svg+xml">
            <title>Claude Code Proxy — {{Esc(s.Date)}}</title>
            <style>
            html { background: #1a1a1a; }
            body { font-family: system-ui, -apple-system, sans-serif; max-width: 980px; margin: 1.5rem auto; padding: 0 1rem; color: #e0e0e0; background: #1a1a1a; }
            h1 { font-size: 1.4rem; margin: 0 0 .25rem; color: #fff; }
            h2 { font-size: 1.05rem; margin: 1.75rem 0 .25rem; border-bottom: 1px solid #333; padding-bottom: .25rem; color: #fff; }
            table { border-collapse: collapse; width: 100%; margin-top: .25rem; }
            th, td { text-align: left; padding: .3rem .55rem; border-bottom: 1px solid #2a2a2a; font-variant-numeric: tabular-nums; }
            th { background: #262626; font-weight: 600; color: #ddd; }
            td.num, th.num { text-align: right; }
            td.mono { font-family: ui-monospace, "SF Mono", Consolas, monospace; font-size: .92rem; }
            .muted { color: #888; font-size: .9rem; }
            nav { margin: .5rem 0 1rem; font-size: .9rem; }
            nav a { color: #9ab; text-decoration: none; margin-right: 1rem; }
            nav a:hover { text-decoration: underline; }
            nav .sep { color: #555; margin-right: 1rem; }
            .pair { margin: .25rem 0; }
            .pair .k { color: #999; margin-right: .5rem; }
            .pair .v { font-weight: 600; color: #fff; }
            .hit { color: #4caf82; }
            .warn { color: #d68a04; }
            .err { color: #e05252; }
            .cost { color: #d4b85e; }
            footer { margin-top: 2rem; font-size: .85rem; color: #777; }
            footer a { color: #9ab; text-decoration: none; }
            footer a:hover { text-decoration: underline; }
            </style>
            </head><body>
            """);

        sb.Append("<h1>Claude Code Proxy</h1>");

        var isHistorical = s.UptimeSeconds < 0;
        var uptimePart = isHistorical ? "historical" : $"uptime: {Esc(FormatUptime(s.UptimeSeconds))}";
        sb.Append($"<p class=\"muted\">stats for {Esc(s.Date)} &middot; {uptimePart}</p>");
        sb.Append("<nav>");
        if (prevDate.HasValue)
            sb.Append($"<a href=\"/stats/{prevDate.Value:yyyy-MM-dd}\">&#8592; {prevDate.Value:yyyy-MM-dd}</a><span class=\"sep\">|</span>");
        if (isHistorical)
            sb.Append("<a href=\"/stats\">Today</a><span class=\"sep\">|</span>");
        sb.Append("<a href=\"/stats/week\">Week</a><span class=\"sep\">|</span>");
        sb.Append("<a href=\"/stats/month\">Month</a>");
        if (nextDate.HasValue)
            sb.Append($"<span class=\"sep\">|</span><a href=\"/stats/{nextDate.Value:yyyy-MM-dd}\">{nextDate.Value:yyyy-MM-dd} &#8594;</a>");
        sb.Append("</nav>");

        sb.Append("<h2>Today</h2>");

        var failedSuffix = s.FailedCount > 0
            ? $" <span class=\"muted err\">({N0(s.FailedCount)} failed)</span>"
            : "";
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests</span><span class=\"v\">{N0(s.RequestCount)}</span>{failedSuffix}</div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">cache hit rate</span><span class=\"v {HitClass(s.HitRatePct)}\">{s.HitRatePct:F0}%</span> <span class=\"muted\">on {N0(totalIn)} input tokens</span></div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">avg latency</span><span class=\"v\">{s.AvgDurationMs:F0} ms</span></div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">total cost</span><span class=\"v cost\">{Money(dayTotalCost)}</span></div>");

        sb.Append("<h2>Token totals</h2>");
        sb.Append("<table><tr><th>type</th><th class=\"num\">tokens</th></tr>");
        sb.Append($"<tr><td class=\"hit\">cache read</td><td class=\"num\">{N0(s.Totals.CacheRead)}</td></tr>");
        sb.Append($"<tr><td class=\"warn\">cache creation</td><td class=\"num\">{N0(s.Totals.CacheCreation)}</td></tr>");
        sb.Append($"<tr><td>uncached input</td><td class=\"num\">{N0(s.Totals.InputUncached)}</td></tr>");
        sb.Append($"<tr><td>output</td><td class=\"num\">{N0(s.Totals.Output)}</td></tr>");
        sb.Append("</table>");

        if (s.ByModel.Count > 0)
        {
            sb.Append("<h2>By model</h2>");
            sb.Append("<table><tr><th>model</th><th class=\"num\">requests</th><th class=\"num\">failed</th><th class=\"num\">hit %</th><th class=\"num\">avg ms</th><th class=\"num\">cache read</th><th class=\"num\">cache create</th><th class=\"num\">uncached</th><th class=\"num\">output</th><th class=\"num\">cost</th></tr>");
            foreach (var (model, m) in s.ByModel.OrderBy(x => x.Key))
            {
                sb.Append($"<tr><td class=\"mono\">{Esc(model)}</td>")
                  .Append($"<td class=\"num\">{N0(m.Requests)}</td>")
                  .Append($"<td class=\"num {(m.Failed > 0 ? "err" : "")}\">{N0(m.Failed)}</td>")
                  .Append($"<td class=\"num {HitClass(m.HitRatePct)}\">{m.HitRatePct:F0}%</td>")
                  .Append($"<td class=\"num\">{m.AvgDurationMs:F0}</td>")
                  .Append($"<td class=\"num\">{N0(m.CacheRead)}</td>")
                  .Append($"<td class=\"num\">{N0(m.CacheCreation)}</td>")
                  .Append($"<td class=\"num\">{N0(m.InputUncached)}</td>")
                  .Append($"<td class=\"num\">{N0(m.Output)}</td>")
                  .Append($"<td class=\"num cost\">{Money(perModelCost[model])}</td></tr>");
            }
            sb.Append("</table>");
        }

        if (s.Hourly.Count > 0)
        {
            sb.Append("<h2>Hourly</h2>");
            sb.Append("<table><tr><th>hour</th><th class=\"num\">requests</th><th class=\"num\">failed</th><th class=\"num\">hit %</th><th class=\"num\">avg ms</th><th class=\"num\">cache read</th><th class=\"num\">cache create</th><th class=\"num\">uncached</th><th class=\"num\">output</th><th class=\"num\">cost*</th></tr>");
            foreach (var h in s.Hourly)
            {
                var hourLabel = h.Hour.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                var hourCost = blended.HasValue
                    ? (h.InputUncached * blended.Value.Input
                       + h.CacheRead * blended.Value.CacheHit
                       + h.CacheCreation5m * blended.Value.CacheWrite5m
                       + h.CacheCreation1h * blended.Value.CacheWrite1h
                       + h.Output * blended.Value.Output) / 1_000_000m
                    : 0m;
                sb.Append($"<tr><td>{Esc(hourLabel)}</td>")
                  .Append($"<td class=\"num\">{N0(h.Requests)}</td>")
                  .Append($"<td class=\"num {(h.FailedCount > 0 ? "err" : "")}\">{N0(h.FailedCount)}</td>")
                  .Append($"<td class=\"num {HitClass(h.HitRatePct)}\">{h.HitRatePct:F0}%</td>")
                  .Append($"<td class=\"num\">{h.AvgDurationMs:F0}</td>")
                  .Append($"<td class=\"num\">{N0(h.CacheRead)}</td>")
                  .Append($"<td class=\"num\">{N0(h.CacheCreation)}</td>")
                  .Append($"<td class=\"num\">{N0(h.InputUncached)}</td>")
                  .Append($"<td class=\"num\">{N0(h.Output)}</td>")
                  .Append($"<td class=\"num cost\">{Money(hourCost)}</td></tr>");
            }
            sb.Append("</table>");
            sb.Append("<p class=\"muted\">* hourly cost uses today's by-model token mix as a blended rate; per-hour exact cost would require per-model hourly tracking.</p>");
        }

        sb.Append("<footer><a href=\"/stats.json\">raw JSON</a></footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string RenderRange(IReadOnlyList<StatsSnapshot> snapshots, PricingCatalog pricing, string title)
    {
        var sb = new StringBuilder(8192);

        long totalRequests = 0, totalFailed = 0;
        long totalUncached = 0, totalRead = 0, totalCc5m = 0, totalCc1h = 0, totalOutput = 0;
        var mergedModels = new Dictionary<string, (long req, long failed, long uncached, long read, long cc5m, long cc1h, long output)>();
        decimal totalCost = 0m;

        foreach (var s in snapshots)
        {
            totalRequests += s.RequestCount;
            totalFailed   += s.FailedCount;
            totalUncached += s.Totals.InputUncached;
            totalRead     += s.Totals.CacheRead;
            totalCc5m     += s.Totals.CacheCreation5m;
            totalCc1h     += s.Totals.CacheCreation1h;
            totalOutput   += s.Totals.Output;

            foreach (var (model, m) in s.ByModel)
            {
                var cur = mergedModels.GetValueOrDefault(model);
                mergedModels[model] = (cur.req + m.Requests, cur.failed + m.Failed,
                    cur.uncached + m.InputUncached, cur.read + m.CacheRead,
                    cur.cc5m + m.CacheCreation5m, cur.cc1h + m.CacheCreation1h,
                    cur.output + m.Output);
            }

            foreach (var (model, m) in s.ByModel)
                totalCost += pricing.CalculateCostUsd(model, m.InputUncached, m.CacheRead, m.CacheCreation5m, m.CacheCreation1h, m.Output);
        }

        var totalIn = totalUncached + totalRead + totalCc5m + totalCc1h;
        var hitPct  = totalIn > 0 ? 100.0 * totalRead / totalIn : 0;

        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><link rel="icon" href="/favicon.svg" type="image/svg+xml">
            <title>Claude Code Proxy — {{Esc(title)}}</title>
            <style>
            html { background: #1a1a1a; }
            body { font-family: system-ui, -apple-system, sans-serif; max-width: 980px; margin: 1.5rem auto; padding: 0 1rem; color: #e0e0e0; background: #1a1a1a; }
            h1 { font-size: 1.4rem; margin: 0 0 .25rem; color: #fff; }
            h2 { font-size: 1.05rem; margin: 1.75rem 0 .25rem; border-bottom: 1px solid #333; padding-bottom: .25rem; color: #fff; }
            table { border-collapse: collapse; width: 100%; margin-top: .25rem; }
            th, td { text-align: left; padding: .3rem .55rem; border-bottom: 1px solid #2a2a2a; font-variant-numeric: tabular-nums; }
            th { background: #262626; font-weight: 600; color: #ddd; }
            td.num, th.num { text-align: right; }
            td.mono { font-family: ui-monospace, "SF Mono", Consolas, monospace; font-size: .92rem; }
            td.date a { color: #e0e0e0; text-decoration: none; }
            td.date a:hover { text-decoration: underline; }
            .muted { color: #888; font-size: .9rem; }
            .pair { margin: .25rem 0; }
            .pair .k { color: #999; margin-right: .5rem; }
            .pair .v { font-weight: 600; color: #fff; }
            .hit { color: #4caf82; }
            .warn { color: #d68a04; }
            .err { color: #e05252; }
            .cost { color: #d4b85e; }
            nav { margin: .5rem 0 1rem; font-size: .9rem; }
            nav a { color: #9ab; text-decoration: none; margin-right: 1rem; }
            nav a:hover { text-decoration: underline; }
            nav .sep { color: #555; margin-right: 1rem; }
            footer { margin-top: 2rem; font-size: .85rem; color: #777; }
            footer a { color: #9ab; text-decoration: none; }
            footer a:hover { text-decoration: underline; }
            </style>
            </head><body>
            """);

        sb.Append("<h1>Claude Code Proxy</h1>");
        sb.Append($"<p class=\"muted\">{Esc(title)} &middot; {snapshots.Count} day{(snapshots.Count == 1 ? "" : "s")} of data</p>");
        sb.Append("<nav>");
        sb.Append("<a href=\"/stats\">Today</a><span class=\"sep\">|</span>");
        sb.Append("<a href=\"/stats/week\">Week</a><span class=\"sep\">|</span>");
        sb.Append("<a href=\"/stats/month\">Month</a>");
        sb.Append("</nav>");

        sb.Append("<h2>Totals</h2>");
        var failedSuffix = totalFailed > 0 ? $" <span class=\"muted err\">({N0(totalFailed)} failed)</span>" : "";
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests</span><span class=\"v\">{N0(totalRequests)}</span>{failedSuffix}</div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">cache hit rate</span><span class=\"v {HitClass(hitPct)}\">{hitPct:F0}%</span> <span class=\"muted\">on {N0(totalIn)} input tokens</span></div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">total cost</span><span class=\"v cost\">{Money(totalCost)}</span></div>");
        sb.Append("<table style=\"margin-top:.75rem\"><tr><th>type</th><th class=\"num\">tokens</th></tr>");
        sb.Append($"<tr><td class=\"hit\">cache read</td><td class=\"num\">{N0(totalRead)}</td></tr>");
        sb.Append($"<tr><td class=\"warn\">cache creation</td><td class=\"num\">{N0(totalCc5m + totalCc1h)}</td></tr>");
        sb.Append($"<tr><td>uncached input</td><td class=\"num\">{N0(totalUncached)}</td></tr>");
        sb.Append($"<tr><td>output</td><td class=\"num\">{N0(totalOutput)}</td></tr>");
        sb.Append("</table>");

        if (snapshots.Count > 0)
        {
            sb.Append("<h2>By day</h2>");
            sb.Append("<table><tr><th>date</th><th class=\"num\">requests</th><th class=\"num\">failed</th><th class=\"num\">hit %</th><th class=\"num\">avg ms</th><th class=\"num\">tokens in</th><th class=\"num\">output</th><th class=\"num\">cost</th></tr>");
            foreach (var s in snapshots.OrderByDescending(x => x.Date))
            {
                var dayIn   = s.Totals.InputUncached + s.Totals.CacheRead + s.Totals.CacheCreation;
                var dayCost = s.ByModel.Sum(kvp =>
                    pricing.CalculateCostUsd(kvp.Key, kvp.Value.InputUncached, kvp.Value.CacheRead, kvp.Value.CacheCreation5m, kvp.Value.CacheCreation1h, kvp.Value.Output));
                var isToday = s.UptimeSeconds >= 0;
                var dateLink = isToday ? "/stats" : $"/stats/{s.Date}";
                sb.Append($"<tr><td class=\"date mono\"><a href=\"{dateLink}\">{Esc(s.Date)}</a>{(isToday ? " <span class=\"muted\">(live)</span>" : "")}</td>")
                  .Append($"<td class=\"num\">{N0(s.RequestCount)}</td>")
                  .Append($"<td class=\"num {(s.FailedCount > 0 ? "err" : "")}\">{N0(s.FailedCount)}</td>")
                  .Append($"<td class=\"num {HitClass(s.HitRatePct)}\">{s.HitRatePct:F0}%</td>")
                  .Append($"<td class=\"num\">{s.AvgDurationMs:F0}</td>")
                  .Append($"<td class=\"num\">{N0(dayIn)}</td>")
                  .Append($"<td class=\"num\">{N0(s.Totals.Output)}</td>")
                  .Append($"<td class=\"num cost\">{Money(dayCost)}</td></tr>");
            }
            sb.Append("</table>");
        }

        if (mergedModels.Count > 0)
        {
            sb.Append("<h2>By model</h2>");
            sb.Append("<table><tr><th>model</th><th class=\"num\">requests</th><th class=\"num\">failed</th><th class=\"num\">hit %</th><th class=\"num\">cache read</th><th class=\"num\">cache create</th><th class=\"num\">uncached</th><th class=\"num\">output</th><th class=\"num\">cost</th></tr>");
            foreach (var (model, m) in mergedModels.OrderBy(x => x.Key))
            {
                var mCc = m.cc5m + m.cc1h;
                var mTotal = m.uncached + m.read + mCc;
                var mHit = mTotal > 0 ? 100.0 * m.read / mTotal : 0;
                var mCost = pricing.CalculateCostUsd(model, m.uncached, m.read, m.cc5m, m.cc1h, m.output);
                sb.Append($"<tr><td class=\"mono\">{Esc(model)}</td>")
                  .Append($"<td class=\"num\">{N0(m.req)}</td>")
                  .Append($"<td class=\"num {(m.failed > 0 ? "err" : "")}\">{N0(m.failed)}</td>")
                  .Append($"<td class=\"num {HitClass(mHit)}\">{mHit:F0}%</td>")
                  .Append($"<td class=\"num\">{N0(m.read)}</td>")
                  .Append($"<td class=\"num\">{N0(mCc)}</td>")
                  .Append($"<td class=\"num\">{N0(m.uncached)}</td>")
                  .Append($"<td class=\"num\">{N0(m.output)}</td>")
                  .Append($"<td class=\"num cost\">{Money(mCost)}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("<footer><a href=\"/stats\">Today</a> &middot; <a href=\"/stats/week\">Week</a> &middot; <a href=\"/stats/month\">Month</a></footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static PricingCatalog.RateBlock? ComputeBlendedRates(StatsSnapshot s, PricingCatalog pricing)
    {
        // Models without pricing entries bias blended cost downward; acceptable since only count-tokens lacks pricing.
        long totIn = 0, totRead = 0, tot5m = 0, tot1h = 0, totOut = 0;
        decimal weightedIn = 0, weightedRead = 0, weighted5m = 0, weighted1h = 0, weightedOut = 0;

        foreach (var (model, m) in s.ByModel)
        {
            totIn += m.InputUncached;
            totRead += m.CacheRead;
            tot5m += m.CacheCreation5m;
            tot1h += m.CacheCreation1h;
            totOut += m.Output;

            if (!pricing.TryGetRates(model, out var r)) continue;
            weightedIn += m.InputUncached * r.Input;
            weightedRead += m.CacheRead * r.CacheHit;
            weighted5m += m.CacheCreation5m * r.CacheWrite5m;
            weighted1h += m.CacheCreation1h * r.CacheWrite1h;
            weightedOut += m.Output * r.Output;
        }

        if (totIn + totRead + tot5m + tot1h + totOut == 0) return null;

        return new PricingCatalog.RateBlock(
            Input: totIn > 0 ? weightedIn / totIn : 0,
            Output: totOut > 0 ? weightedOut / totOut : 0,
            CacheWrite5m: tot5m > 0 ? weighted5m / tot5m : 0,
            CacheWrite1h: tot1h > 0 ? weighted1h / tot1h : 0,
            CacheHit: totRead > 0 ? weightedRead / totRead : 0
        );
    }

    private static string Esc(string? s) => HtmlEncoder.Default.Encode(s ?? "");
    private static string N0(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string HitClass(double pct) => pct >= 70 ? "hit" : pct >= 30 ? "warn" : "";

    private static string Money(decimal usd)
    {
        // Sub-cent precision for tiny costs so they don't all collapse to $0.00.
        if (usd == 0m) return "$0.00";
        if (usd < 0.01m) return "$" + usd.ToString("F4", CultureInfo.InvariantCulture);
        return "$" + usd.ToString("N2", CultureInfo.InvariantCulture);
    }

    private static string FormatUptime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalSeconds}s";
    }
}
