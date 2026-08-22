using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace ClaudeCodeProxy;

internal static class StatsHtmlRenderer
{
    public static string Render(StatsSnapshot s, PricingCatalog pricing, string version,
        DateOnly? prevDate = null, DateOnly? nextDate = null, string? provider = null,
        IReadOnlyList<StatsSnapshot>? currentMonthSnapshots = null, decimal monthlyTargetSpendUsd = 5500m,
        SessionMetadataSnapshot? sessionMetadata = null)
    {
        var filteredModels = FilteredModels(s, provider);
        var snapshotDate = SnapshotDate(s);

        var perModelCost = new Dictionary<string, decimal>();
        long filteredRequests = 0, filteredFailed = 0;
        long filteredUncached = 0, filteredRead = 0, filteredCc5m = 0, filteredCc1h = 0, filteredOutput = 0;
        decimal dayTotalCost = 0m;
        foreach (var (key, m) in filteredModels)
        {
            var modelName = m.Model ?? key;
            filteredRequests += m.Requests;
            filteredFailed   += m.Failed;
            filteredUncached += m.InputUncached;
            filteredRead     += m.CacheRead;
            filteredCc5m     += m.CacheCreation5m;
            filteredCc1h     += m.CacheCreation1h;
            filteredOutput   += m.Output;
            var cost = pricing.CalculateCostUsd(modelName, m.InputUncached, m.CacheRead, m.CacheCreation5m, m.CacheCreation1h, m.Output, snapshotDate);
            perModelCost[key] = cost;
            dayTotalCost += cost;
        }
        var filteredTotalIn = filteredUncached + filteredRead + filteredCc5m + filteredCc1h;
        var filteredHitPct  = filteredTotalIn > 0 ? 100.0 * filteredRead / filteredTotalIn : 0;

        // Blended rate uses filtered models only so hourly cost footnote reflects the active filter.
        var blended = ComputeBlendedRates(filteredModels, pricing, snapshotDate);

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
            .page-header { display: flex; align-items: center; gap: 1.5rem; }
            .page-header-text { flex: 1; min-width: 0; }
            .page-header-text > p { margin-top: .15rem; }
            .page-header-icon { flex-shrink: 0; }
            .page-header-icon svg { display: block; height: 3.5rem; width: auto; }
            .failure-warn { display: flex; align-items: center; gap: 1rem; background: #2d1010; border: 1px solid #e05252; border-radius: 4px; padding: .5rem .75rem; margin: .75rem 0; color: #e05252; font-size: .9rem; }
            .failure-warn > span { flex: 1; min-width: 0; }
            .dismiss-btn { flex-shrink: 0; background: #3a3a3a; border: 1px solid #555; color: #ccc; border-radius: 3px; padding: .15rem .6rem; font-size: .82rem; cursor: pointer; white-space: nowrap; }
            nav { display: flex; align-items: baseline; justify-content: space-between; }
            .nav-left { display: inline; }
            .nav-right { display: flex; gap: .4rem; align-items: center; }
            .prov-btn { color: #9ab; text-decoration: none; margin-right: 0; padding: .1rem .45rem; border: 1px solid #333; border-radius: 3px; font-size: .85rem; }
            .prov-btn:hover { border-color: #555; text-decoration: none; }
            .prov-active { color: #fff; border-color: #555; background: #2d2d2d; }
            .session-total td { background: #222; border-top: 1px solid #444; }
            .agent-label { display: inline-block; overflow-wrap: anywhere; }
            .session-id { overflow-wrap: anywhere; }
            </style>
            </head><body>
            """);

        sb.Append("<div class=\"page-header\"><div class=\"page-header-text\">");
        sb.Append("<h1>Claude Code Proxy</h1>");

        var isHistorical = s.UptimeSeconds < 0;
        var uptimePart = isHistorical ? "historical" : $"uptime: {Esc(FormatUptime(s.UptimeSeconds))}";
        sb.Append($"<p class=\"muted\">stats for {Esc(s.Date)} &middot; {uptimePart}</p>");
        sb.Append("</div><div class=\"page-header-icon\">").Append(HeaderIconSvg).Append("</div></div>");

        AppendFailureBanners(sb, s.RewriteFailures, s.AcknowledgedFailures, s.UnknownPaths);

        var basePath = isHistorical ? $"/stats/{s.Date}" : "/stats";
        var provQ = provider is not null ? $"?provider={provider}" : "";
        sb.Append("<nav><span class=\"nav-left\">");
        if (prevDate.HasValue)
            sb.Append($"<a href=\"/stats/{prevDate.Value:yyyy-MM-dd}{provQ}\">&#8592; {prevDate.Value:yyyy-MM-dd}</a><span class=\"sep\">|</span>");
        if (isHistorical)
            sb.Append($"<a href=\"/stats{provQ}\">Today</a><span class=\"sep\">|</span>");
        sb.Append($"<a href=\"/stats/week{provQ}\">Week</a><span class=\"sep\">|</span>");
        sb.Append($"<a href=\"/stats/month{provQ}\">Month</a>");
        if (nextDate.HasValue)
            sb.Append($"<span class=\"sep\">|</span><a href=\"/stats/{nextDate.Value:yyyy-MM-dd}{provQ}\">{nextDate.Value:yyyy-MM-dd} &#8594;</a>");
        sb.Append("</span>");
        AppendProviderButtons(sb, basePath, provider);
        sb.Append("</nav>");

        var sectionTitle = provider is not null ? $"Today ({provider})" : "Today";
        sb.Append($"<h2>{sectionTitle}</h2>");

        var failedSuffix = filteredFailed > 0
            ? $" <span class=\"muted err\">({N0(filteredFailed)} failed)</span>"
            : "";
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests</span><span class=\"v\">{N0(filteredRequests)}</span>{failedSuffix}</div>");
        var promptCountScope = provider is not null ? " <span class=\"muted\">(all providers)</span>" : "";
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests injected</span><span class=\"v\">{N0(s.InjectedRequests)}</span>{promptCountScope}</div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests passed through</span><span class=\"v\">{N0(s.PassedThroughRequests)}</span>{promptCountScope}</div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">cache hit rate</span><span class=\"v {HitClass(filteredHitPct)}\">{filteredHitPct:F0}%</span> <span class=\"muted\">on {N0(filteredTotalIn)} input tokens</span></div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">avg latency</span><span class=\"v\">{s.AvgDurationMs:F0} ms</span></div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">total cost</span><span class=\"v cost\">{Money(dayTotalCost)}</span></div>");

        sb.Append("<h2>Token totals</h2>");
        sb.Append("<table><tr><th>type</th><th class=\"num\">tokens</th></tr>");
        sb.Append($"<tr><td class=\"hit\">cache read</td><td class=\"num\">{N0(filteredRead)}</td></tr>");
        sb.Append($"<tr><td class=\"warn\">cache creation</td><td class=\"num\">{N0(filteredCc5m + filteredCc1h)}</td></tr>");
        sb.Append($"<tr><td>uncached input</td><td class=\"num\">{N0(filteredUncached)}</td></tr>");
        sb.Append($"<tr><td>output</td><td class=\"num\">{N0(filteredOutput)}</td></tr>");
        sb.Append("</table>");

        if (filteredModels.Count > 0)
        {
            sb.Append("<h2>By model</h2>");
            sb.Append("<table><tr><th>model</th><th class=\"num\">requests</th><th class=\"num\">failed</th><th class=\"num\">hit %</th><th class=\"num\">avg ms</th><th class=\"num\">cache read</th><th class=\"num\">cache create</th><th class=\"num\">uncached</th><th class=\"num\">output</th><th class=\"num\">cost</th></tr>");
            foreach (var (key, m) in filteredModels.OrderBy(x => x.Value.Model ?? x.Key).ThenBy(x => x.Value.Provider))
            {
                var modelName = m.Model ?? key;
                var providerSuffix = provider is null && m.Provider is not null
                    ? $" <span class=\"muted\">({Esc(m.Provider)})</span>"
                    : "";
                sb.Append($"<tr><td class=\"mono\">{Esc(modelName)}{providerSuffix}</td>")
                  .Append($"<td class=\"num\">{N0(m.Requests)}</td>")
                  .Append($"<td class=\"num {(m.Failed > 0 ? "err" : "")}\">{N0(m.Failed)}</td>")
                  .Append($"<td class=\"num {HitClass(m.HitRatePct)}\">{m.HitRatePct:F0}%</td>")
                  .Append($"<td class=\"num\">{m.AvgDurationMs:F0}</td>")
                  .Append($"<td class=\"num\">{N0(m.CacheRead)}</td>")
                  .Append($"<td class=\"num\">{N0(m.CacheCreation)}</td>")
                  .Append($"<td class=\"num\">{N0(m.InputUncached)}</td>")
                  .Append($"<td class=\"num\">{N0(m.Output)}</td>")
                  .Append($"<td class=\"num cost\">{Money(perModelCost[key])}</td></tr>");
            }
            sb.Append("</table>");
        }

        if (currentMonthSnapshots is not null && snapshotDate is { } currentDate)
            AppendCurrentMonthSpend(sb, currentMonthSnapshots, pricing, provider, currentDate, monthlyTargetSpendUsd);

        AppendSessionSection(sb, BuildSessionRows([(s, snapshotDate)], pricing, provider), sessionMetadata);

        var filteredEndpoints = s.ByEndpoint
            .Where(kvp => provider is null || kvp.Value.Provider == provider)
            .OrderBy(kvp => kvp.Value.Provider)
            .ThenBy(kvp => kvp.Key)
            .ToList();
        if (filteredEndpoints.Count > 0)
        {
            sb.Append("<h2>Endpoint routing</h2>");
            if (provider is null)
                sb.Append("<table><tr><th>provider</th><th>endpoint</th><th class=\"num\">requests</th><th class=\"num\">failed</th></tr>");
            else
                sb.Append("<table><tr><th>endpoint</th><th class=\"num\">requests</th><th class=\"num\">failed</th></tr>");
            foreach (var (url, ep) in filteredEndpoints)
            {
                if (provider is null)
                    sb.Append($"<tr><td>{(ep.Provider is not null ? Esc(ep.Provider) : "&mdash;")}</td>")
                      .Append($"<td class=\"mono\">{Esc(url)}</td>")
                      .Append($"<td class=\"num\">{N0(ep.Requests)}</td>")
                      .Append($"<td class=\"num {(ep.Failed > 0 ? "err" : "")}\">{N0(ep.Failed)}</td></tr>");
                else
                    sb.Append($"<tr><td class=\"mono\">{Esc(url)}</td>")
                      .Append($"<td class=\"num\">{N0(ep.Requests)}</td>")
                      .Append($"<td class=\"num {(ep.Failed > 0 ? "err" : "")}\">{N0(ep.Failed)}</td></tr>");
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
                var ph = provider is not null ? h.ByProvider?.GetValueOrDefault(provider) : null;
                var rowReq      = ph?.Requests      ?? h.Requests;
                var rowFailed   = ph?.FailedCount   ?? h.FailedCount;
                var rowUncached = ph?.InputUncached ?? h.InputUncached;
                var rowRead     = ph?.CacheRead     ?? h.CacheRead;
                var rowCc5m     = ph?.CacheCreation5m ?? h.CacheCreation5m;
                var rowCc1h     = ph?.CacheCreation1h ?? h.CacheCreation1h;
                var rowOutput   = ph?.Output        ?? h.Output;
                var rowHitPct   = ph?.HitRatePct    ?? h.HitRatePct;
                var rowAvgMs    = ph?.AvgDurationMs ?? h.AvgDurationMs;
                var hourCost = blended.HasValue
                    ? (rowUncached * blended.Value.Input
                       + rowRead    * blended.Value.CacheHit
                       + rowCc5m   * blended.Value.CacheWrite5m
                       + rowCc1h   * blended.Value.CacheWrite1h
                       + rowOutput * blended.Value.Output) / 1_000_000m
                    : 0m;
                sb.Append($"<tr><td>{Esc(hourLabel)}</td>")
                  .Append($"<td class=\"num\">{N0(rowReq)}</td>")
                  .Append($"<td class=\"num {(rowFailed > 0 ? "err" : "")}\">{N0(rowFailed)}</td>")
                  .Append($"<td class=\"num {HitClass(rowHitPct)}\">{rowHitPct:F0}%</td>")
                  .Append($"<td class=\"num\">{rowAvgMs:F0}</td>")
                  .Append($"<td class=\"num\">{N0(rowRead)}</td>")
                  .Append($"<td class=\"num\">{N0(rowCc5m + rowCc1h)}</td>")
                  .Append($"<td class=\"num\">{N0(rowUncached)}</td>")
                  .Append($"<td class=\"num\">{N0(rowOutput)}</td>")
                  .Append($"<td class=\"num cost\">{Money(hourCost)}</td></tr>");
            }
            sb.Append("</table>");
            sb.Append("<p class=\"muted\">* hourly cost uses today's by-model token mix as a blended rate; per-hour exact cost would require per-model hourly tracking.</p>");
        }

        sb.Append($"<footer><a href=\"/stats.json\">raw JSON</a> <span style=\"color:#555\">v{Esc(version)}</span></footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string RenderRange(IReadOnlyList<StatsSnapshot> snapshots, PricingCatalog pricing, string title,
        string currentPath, string version, string? provider = null, SessionMetadataSnapshot? sessionMetadata = null)
    {
        var sb = new StringBuilder(8192);

        long totalRequests = 0, totalFailed = 0, totalInjected = 0, totalPassedThrough = 0;
        long totalUncached = 0, totalRead = 0, totalCc5m = 0, totalCc1h = 0, totalOutput = 0;
        var mergedModels = new Dictionary<string, (long req, long failed, long uncached, long read, long cc5m, long cc1h, long output)>();
        var modelNames = new Dictionary<string, string>();        // composite key -> bare model name
        var modelProviders = new Dictionary<string, string?>();   // composite key -> provider
        var mergedEndpoints = new Dictionary<string, (long req, long failed)>();
        var endpointProviders = new Dictionary<string, string?>();
        decimal totalCost = 0m;

        // Costs accumulate per snapshot so each day is priced with the rates in effect on that day.
        var mergedCosts = new Dictionary<string, decimal>();
        foreach (var s in snapshots)
        {
            totalInjected += s.InjectedRequests;
            totalPassedThrough += s.PassedThroughRequests;
            var fm = FilteredModels(s, provider);
            var snapshotDate = SnapshotDate(s);
            foreach (var (key, m) in fm)
            {
                var modelName = m.Model ?? key;
                totalRequests += m.Requests;
                totalFailed   += m.Failed;
                totalUncached += m.InputUncached;
                totalRead     += m.CacheRead;
                totalCc5m     += m.CacheCreation5m;
                totalCc1h     += m.CacheCreation1h;
                totalOutput   += m.Output;
                var cur = mergedModels.GetValueOrDefault(key);
                mergedModels[key] = (cur.req + m.Requests, cur.failed + m.Failed,
                    cur.uncached + m.InputUncached, cur.read + m.CacheRead,
                    cur.cc5m + m.CacheCreation5m, cur.cc1h + m.CacheCreation1h,
                    cur.output + m.Output);
                modelNames[key] = modelName;
                modelProviders[key] = m.Provider;
                var cost = pricing.CalculateCostUsd(modelName, m.InputUncached, m.CacheRead, m.CacheCreation5m, m.CacheCreation1h, m.Output, snapshotDate);
                mergedCosts[key] = mergedCosts.GetValueOrDefault(key) + cost;
                totalCost += cost;
            }
            foreach (var (url, ep) in s.ByEndpoint)
            {
                if (provider is not null && ep.Provider != provider) continue;
                var cur = mergedEndpoints.GetValueOrDefault(url);
                mergedEndpoints[url] = (cur.req + ep.Requests, cur.failed + ep.Failed);
                if (!endpointProviders.ContainsKey(url) || endpointProviders[url] is null)
                    endpointProviders[url] = ep.Provider;
            }
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
            .page-header { display: flex; align-items: center; gap: 1.5rem; }
            .page-header-text { flex: 1; min-width: 0; }
            .page-header-text > p { margin-top: .15rem; }
            .page-header-icon { flex-shrink: 0; }
            .page-header-icon svg { display: block; height: 3.5rem; width: auto; }
            .failure-warn { display: flex; align-items: center; gap: 1rem; background: #2d1010; border: 1px solid #e05252; border-radius: 4px; padding: .5rem .75rem; margin: .75rem 0; color: #e05252; font-size: .9rem; }
            .failure-warn > span { flex: 1; min-width: 0; }
            .dismiss-btn { flex-shrink: 0; background: #3a3a3a; border: 1px solid #555; color: #ccc; border-radius: 3px; padding: .15rem .6rem; font-size: .82rem; cursor: pointer; white-space: nowrap; }
            nav { display: flex; align-items: baseline; justify-content: space-between; }
            .nav-left { display: inline; }
            .nav-right { display: flex; gap: .4rem; align-items: center; }
            .prov-btn { color: #9ab; text-decoration: none; margin-right: 0; padding: .1rem .45rem; border: 1px solid #333; border-radius: 3px; font-size: .85rem; }
            .prov-btn:hover { border-color: #555; text-decoration: none; }
            .prov-active { color: #fff; border-color: #555; background: #2d2d2d; }
            .session-total td { background: #222; border-top: 1px solid #444; }
            .agent-label { display: inline-block; overflow-wrap: anywhere; }
            .session-id { overflow-wrap: anywhere; }
            </style>
            </head><body>
            """);

        sb.Append("<div class=\"page-header\"><div class=\"page-header-text\">");
        sb.Append("<h1>Claude Code Proxy</h1>");
        sb.Append($"<p class=\"muted\">{Esc(title)} &middot; {snapshots.Count} day{(snapshots.Count == 1 ? "" : "s")} of data</p>");
        sb.Append("</div><div class=\"page-header-icon\">").Append(HeaderIconSvg).Append("</div></div>");

        var aggregatedFailures = Enum.GetValues<PromptFailureMode>()
            .ToDictionary(m => m, m => snapshots.Sum(s => s.RewriteFailures.GetValueOrDefault(m)));
        var aggregatedAcknowledged = Enum.GetValues<PromptFailureMode>()
            .ToDictionary(m => m, m => snapshots.Sum(s => s.AcknowledgedFailures.GetValueOrDefault(m)));
        var aggregatedUnknownPaths = snapshots.SelectMany(s => s.UnknownPaths).Distinct().ToList();
        AppendFailureBanners(sb, aggregatedFailures, aggregatedAcknowledged, aggregatedUnknownPaths);
        var provQ = provider is not null ? $"?provider={provider}" : "";
        sb.Append("<nav><span class=\"nav-left\">");
        sb.Append($"<a href=\"/stats{provQ}\">Today</a><span class=\"sep\">|</span>");
        sb.Append($"<a href=\"/stats/week{provQ}\">Week</a><span class=\"sep\">|</span>");
        sb.Append($"<a href=\"/stats/month{provQ}\">Month</a>");
        sb.Append("</span>");
        AppendProviderButtons(sb, currentPath, provider);
        sb.Append("</nav>");

        sb.Append("<h2>Totals</h2>");
        var failedSuffix = totalFailed > 0 ? $" <span class=\"muted err\">({N0(totalFailed)} failed)</span>" : "";
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests</span><span class=\"v\">{N0(totalRequests)}</span>{failedSuffix}</div>");
        var promptCountScope = provider is not null ? " <span class=\"muted\">(all providers)</span>" : "";
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests injected</span><span class=\"v\">{N0(totalInjected)}</span>{promptCountScope}</div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">requests passed through</span><span class=\"v\">{N0(totalPassedThrough)}</span>{promptCountScope}</div>");
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
                var dayFm = FilteredModels(s, provider);
                var dayDate = SnapshotDate(s);
                long dayReq = 0, dayFailed = 0, dayUncached = 0, dayRead = 0, dayCc5m = 0, dayCc1h = 0, dayOutput = 0;
                decimal dayCost = 0m;
                foreach (var (key, m) in dayFm)
                {
                    var modelName = m.Model ?? key;
                    dayReq     += m.Requests;
                    dayFailed  += m.Failed;
                    dayUncached += m.InputUncached;
                    dayRead    += m.CacheRead;
                    dayCc5m    += m.CacheCreation5m;
                    dayCc1h    += m.CacheCreation1h;
                    dayOutput  += m.Output;
                    dayCost    += pricing.CalculateCostUsd(modelName, m.InputUncached, m.CacheRead, m.CacheCreation5m, m.CacheCreation1h, m.Output, dayDate);
                }
                var dayIn   = dayUncached + dayRead + dayCc5m + dayCc1h;
                var dayHit  = dayIn > 0 ? 100.0 * dayRead / dayIn : 0;
                var isToday = s.UptimeSeconds >= 0;
                var dateLink = isToday ? $"/stats{provQ}" : $"/stats/{s.Date}{provQ}";
                sb.Append($"<tr><td class=\"date mono\"><a href=\"{dateLink}\">{Esc(s.Date)}</a>{(isToday ? " <span class=\"muted\">(live)</span>" : "")}</td>")
                  .Append($"<td class=\"num\">{N0(dayReq)}</td>")
                  .Append($"<td class=\"num {(dayFailed > 0 ? "err" : "")}\">{N0(dayFailed)}</td>")
                  .Append($"<td class=\"num {HitClass(dayHit)}\">{dayHit:F0}%</td>")
                  .Append($"<td class=\"num\">{s.AvgDurationMs:F0}</td>")
                  .Append($"<td class=\"num\">{N0(dayIn)}</td>")
                  .Append($"<td class=\"num\">{N0(dayOutput)}</td>")
                  .Append($"<td class=\"num cost\">{Money(dayCost)}</td></tr>");
            }
            sb.Append("</table>");
        }

        if (mergedModels.Count > 0)
        {
            sb.Append("<h2>By model</h2>");
            sb.Append("<table><tr><th>model</th><th class=\"num\">requests</th><th class=\"num\">failed</th><th class=\"num\">hit %</th><th class=\"num\">cache read</th><th class=\"num\">cache create</th><th class=\"num\">uncached</th><th class=\"num\">output</th><th class=\"num\">cost</th></tr>");
            foreach (var (key, m) in mergedModels.OrderBy(x => modelNames[x.Key]).ThenBy(x => modelProviders[x.Key]))
            {
                var modelName = modelNames[key];
                var providerSuffix = provider is null && modelProviders[key] is not null
                    ? $" <span class=\"muted\">({Esc(modelProviders[key]!)})</span>"
                    : "";
                var mCc = m.cc5m + m.cc1h;
                var mTotal = m.uncached + m.read + mCc;
                var mHit = mTotal > 0 ? 100.0 * m.read / mTotal : 0;
                var mCost = mergedCosts.GetValueOrDefault(key);
                sb.Append($"<tr><td class=\"mono\">{Esc(modelName)}{providerSuffix}</td>")
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

        AppendSessionSection(sb, BuildSessionRows(snapshots.Select(s => (s, SnapshotDate(s))), pricing, provider), sessionMetadata);

        if (mergedEndpoints.Count > 0)
        {
            sb.Append("<h2>Endpoint routing</h2>");
            if (provider is null)
                sb.Append("<table><tr><th>provider</th><th>endpoint</th><th class=\"num\">requests</th><th class=\"num\">failed</th></tr>");
            else
                sb.Append("<table><tr><th>endpoint</th><th class=\"num\">requests</th><th class=\"num\">failed</th></tr>");
            foreach (var (url, ep) in mergedEndpoints
                .OrderBy(x => endpointProviders[x.Key])
                .ThenBy(x => x.Key))
            {
                if (provider is null)
                    sb.Append($"<tr><td>{(endpointProviders[url] is not null ? Esc(endpointProviders[url]!) : "&mdash;")}</td>")
                      .Append($"<td class=\"mono\">{Esc(url)}</td>")
                      .Append($"<td class=\"num\">{N0(ep.req)}</td>")
                      .Append($"<td class=\"num {(ep.failed > 0 ? "err" : "")}\">{N0(ep.failed)}</td></tr>");
                else
                    sb.Append($"<tr><td class=\"mono\">{Esc(url)}</td>")
                      .Append($"<td class=\"num\">{N0(ep.req)}</td>")
                      .Append($"<td class=\"num {(ep.failed > 0 ? "err" : "")}\">{N0(ep.failed)}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append($"<footer><a href=\"/stats\">Today</a> &middot; <a href=\"/stats/week\">Week</a> &middot; <a href=\"/stats/month\">Month</a> <span style=\"color:#555\">v{Esc(version)}</span></footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, ModelSnapshot> FilteredModels(StatsSnapshot s, string? provider) =>
        provider is null
            ? s.ByModel
            : s.ByModel
                .Where(kvp => kvp.Value.Provider == provider)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    private static IReadOnlyList<SessionRow> BuildSessionRows(
        IEnumerable<(StatsSnapshot snapshot, DateOnly? snapshotDate)> snapshots,
        PricingCatalog pricing,
        string? provider)
    {
        var rows = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
        foreach (var (snapshot, snapshotDate) in snapshots)
        {
            foreach (var session in snapshot.Sessions ?? [])
            {
                // An empty key is safe here: Claude Code's real session IDs are non-empty UUIDs.
                var sessionKey = session.SessionId ?? "";
                if (!rows.TryGetValue(sessionKey, out var sessionRow))
                    rows[sessionKey] = sessionRow = new SessionRow(session.SessionId);

                foreach (var agent in session.Agents)
                {
                    var metrics = FilteredAgentMetrics(agent, provider);
                    if (!metrics.HasData) continue;

                    var agentKey = agent.AgentId ?? "";
                    if (!sessionRow.Agents.TryGetValue(agentKey, out var agentRow))
                    {
                        sessionRow.Agents[agentKey] = agentRow = new AgentRow(agent.AgentId, agent.ParentAgentId);
                    }
                    else if (agentRow.ParentAgentId is null && agent.ParentAgentId is not null)
                    {
                        agentRow.ParentAgentId = agent.ParentAgentId;
                    }

                    var cost = agent.ByModel.Values
                        .Where(m => provider is null || m.Provider == provider)
                        .Sum(m => pricing.CalculateCostUsd(m.Model, m.InputUncached, m.CacheRead,
                            m.CacheCreation5m, m.CacheCreation1h, m.Output, snapshotDate));
                    agentRow.Add(metrics, cost);
                }
            }
        }

        return rows.Values
            .Where(s => s.Metrics.HasData)
            .OrderBy(s => s.SessionId is null ? 1 : 0)
            .ThenByDescending(s => s.Cost)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    private static SessionMetrics FilteredAgentMetrics(AgentSnapshot agent, string? provider)
    {
        if (provider is null)
            return new SessionMetrics(agent.Requests, agent.Failed, agent.Totals);

        return agent.ByProvider.TryGetValue(provider, out var p)
            ? new SessionMetrics(p.Requests, p.Failed,
                new TokenTotals(p.InputUncached, p.CacheRead, p.CacheCreation5m, p.CacheCreation1h, p.Output))
            : SessionMetrics.Empty;
    }

    private static void AppendSessionSection(
        StringBuilder sb,
        IReadOnlyList<SessionRow> sessions,
        SessionMetadataSnapshot? metadata)
    {
        if (sessions.Count == 0) return;

        sb.Append("<h2>Claude Code sessions</h2>");
        sb.Append("<table><tr><th>session / agent</th><th class=\"num\">requests</th><th class=\"num\">failed</th><th class=\"num\">tokens in</th><th class=\"num\">output</th><th class=\"num\">cost</th></tr>");
        foreach (var session in sessions)
        {
            var metrics = session.Metrics;
            var sessionMetadata = session.SessionId is not null
                && metadata?.Sessions.TryGetValue(session.SessionId, out var foundSessionMetadata) == true
                ? foundSessionMetadata
                : null;
            var title = session.SessionId is null
                ? "<strong>Unattributed</strong><div class=\"muted\">no Claude Code session ID</div>"
                : SessionTitle(session.SessionId, sessionMetadata);
            AppendSessionRow(sb, "session-total", title, metrics, session.Cost);

            // Requests without a session ID cannot be assigned to a reliable hierarchy.
            if (session.SessionId is not null)
                AppendAgentHierarchy(sb, session, sessionMetadata);
        }
        sb.Append("</table>");
    }

    private static string SessionTitle(string sessionId, SessionMetadataSessionSnapshot? metadata)
    {
        var displayName = metadata?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = FallbackSessionName(sessionId, metadata?.LastCwd);

        return $"<strong>{Esc(displayName)}</strong><div class=\"muted\">session <span class=\"mono session-id\">{Esc(sessionId)}</span></div>";
    }

    private static string FallbackSessionName(string sessionId, string? cwd)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(leaf))
                return $"{leaf} ({ShortId(sessionId)})";
        }

        return "Session";
    }

    private static string ShortId(string id) => id[..Math.Min(id.Length, 8)];

    private static void AppendAgentHierarchy(
        StringBuilder sb,
        SessionRow session,
        SessionMetadataSessionSnapshot? metadata)
    {
        var agents = session.Agents.Values.ToList();
        var byId = agents
            .Where(a => a.AgentId is not null)
            .ToDictionary(a => a.AgentId!, StringComparer.Ordinal);
        var children = agents
            .Where(a => a.AgentId is not null && a.ParentAgentId is not null && byId.ContainsKey(a.ParentAgentId))
            .GroupBy(a => a.ParentAgentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.AgentId, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        if (session.Agents.TryGetValue("", out var mainAgent))
            AppendAgentBranch(sb, mainAgent, depth: 1, parentIsMissing: false, children, visited, metadata?.Agents);

        foreach (var agent in agents
            .Where(a => a.AgentId is not null && (a.ParentAgentId is null || !byId.ContainsKey(a.ParentAgentId)))
            .OrderBy(a => a.AgentId, StringComparer.Ordinal))
        {
            AppendAgentBranch(sb, agent, depth: 1, parentIsMissing: agent.ParentAgentId is not null, children, visited, metadata?.Agents);
        }

        // Cyclic or otherwise malformed parent references still remain visible instead of disappearing.
        foreach (var agent in agents.Where(a => a.AgentId is not null).OrderBy(a => a.AgentId, StringComparer.Ordinal))
        {
            if (!visited.Contains(agent.AgentId!))
                AppendAgentBranch(sb, agent, depth: 1, parentIsMissing: false, children, visited, metadata?.Agents);
        }
    }

    private static void AppendAgentBranch(
        StringBuilder sb,
        AgentRow agent,
        int depth,
        bool parentIsMissing,
        IReadOnlyDictionary<string, List<AgentRow>> children,
        ISet<string> visited,
        IReadOnlyDictionary<string, AgentMetadataSnapshot>? metadata)
    {
        if (agent.AgentId is not null && !visited.Add(agent.AgentId)) return;

        var padding = (depth * 1.25m).ToString("0.##", CultureInfo.InvariantCulture);
        var title = new StringBuilder($"<span class=\"agent-label\" style=\"padding-left:{padding}rem\">");
        if (agent.AgentId is null)
        {
            title.Append("<strong>Main agent</strong>");
        }
        else
        {
            var agentType = metadata?.TryGetValue(agent.AgentId, out var agentMetadata) == true
                ? agentMetadata.AgentType
                : null;
            if (string.IsNullOrWhiteSpace(agentType))
                title.Append("Agent <span class=\"mono\">").Append(Esc(agent.AgentId)).Append("</span>");
            else
                title.Append("<strong>").Append(Esc(agentType)).Append("</strong><div class=\"muted\">agent <span class=\"mono\">")
                    .Append(Esc(agent.AgentId)).Append("</span></div>");
            if (parentIsMissing && agent.ParentAgentId is not null)
                title.Append(" <span class=\"muted\">parent: ").Append(Esc(agent.ParentAgentId)).Append(" (not observed)</span>");
        }
        title.Append("</span>");
        AppendSessionRow(sb, "", title.ToString(), agent.Metrics, agent.Cost);

        if (agent.AgentId is not null && children.TryGetValue(agent.AgentId, out var childAgents))
            foreach (var child in childAgents)
                AppendAgentBranch(sb, child, depth + 1, parentIsMissing: false, children, visited, metadata);
    }

    private static void AppendSessionRow(StringBuilder sb, string rowClass, string title, SessionMetrics metrics, decimal cost)
    {
        var input = metrics.Totals.InputUncached + metrics.Totals.CacheRead + metrics.Totals.CacheCreation;
        sb.Append($"<tr class=\"{rowClass}\"><td>{title}</td>")
          .Append($"<td class=\"num\">{N0(metrics.Requests)}</td>")
          .Append($"<td class=\"num {(metrics.Failed > 0 ? "err" : "")}\">{N0(metrics.Failed)}</td>")
          .Append($"<td class=\"num\">{N0(input)}</td>")
          .Append($"<td class=\"num\">{N0(metrics.Totals.Output)}</td>")
          .Append($"<td class=\"num cost\">{Money(cost)}</td></tr>");
    }

    private sealed class SessionRow(string? sessionId)
    {
        public string? SessionId { get; } = sessionId;
        public Dictionary<string, AgentRow> Agents { get; } = new(StringComparer.Ordinal);
        public SessionMetrics Metrics => Agents.Values.Aggregate(SessionMetrics.Empty, (sum, a) => sum + a.Metrics);
        public decimal Cost => Agents.Values.Sum(a => a.Cost);
    }

    private sealed class AgentRow(string? agentId, string? parentAgentId)
    {
        public string? AgentId { get; } = agentId;
        public string? ParentAgentId { get; set; } = parentAgentId;
        public SessionMetrics Metrics { get; private set; } = SessionMetrics.Empty;
        public decimal Cost { get; private set; }

        public void Add(SessionMetrics metrics, decimal cost)
        {
            Metrics += metrics;
            Cost += cost;
        }
    }

    private readonly record struct SessionMetrics(long Requests, long Failed, TokenTotals Totals)
    {
        public static SessionMetrics Empty { get; } = new(0, 0, new TokenTotals(0, 0, 0, 0, 0));
        public bool HasData => Requests != 0 || Failed != 0 || Totals.InputUncached != 0 || Totals.CacheRead != 0
            || Totals.CacheCreation != 0 || Totals.Output != 0;
        public static SessionMetrics operator +(SessionMetrics left, SessionMetrics right) =>
            new(left.Requests + right.Requests, left.Failed + right.Failed,
                new TokenTotals(left.Totals.InputUncached + right.Totals.InputUncached,
                    left.Totals.CacheRead + right.Totals.CacheRead,
                    left.Totals.CacheCreation5m + right.Totals.CacheCreation5m,
                    left.Totals.CacheCreation1h + right.Totals.CacheCreation1h,
                    left.Totals.Output + right.Totals.Output));
    }

    private static void AppendCurrentMonthSpend(
        StringBuilder sb,
        IReadOnlyList<StatsSnapshot> snapshots,
        PricingCatalog pricing,
        string? provider,
        DateOnly currentDate,
        decimal monthlyTargetSpendUsd)
    {
        decimal monthToDateCost = 0m;
        foreach (var snapshot in snapshots)
        {
            var snapshotDate = SnapshotDate(snapshot);
            foreach (var (key, model) in FilteredModels(snapshot, provider))
            {
                var modelName = model.Model ?? key;
                monthToDateCost += pricing.CalculateCostUsd(modelName, model.InputUncached, model.CacheRead,
                    model.CacheCreation5m, model.CacheCreation1h, model.Output, snapshotDate);
            }
        }

        var daysInMonth = DateTime.DaysInMonth(currentDate.Year, currentDate.Month);
        var dailyAverage = monthToDateCost / currentDate.Day;
        var dailyTarget = monthlyTargetSpendUsd / daysInMonth;
        var monthName = currentDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        sb.Append($"<h2>{Esc(monthName)} daily spend</h2>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">daily average cost</span><span class=\"v cost\">{Money(dailyAverage)}</span> <span class=\"muted\">month-to-date across {currentDate.Day} day{(currentDate.Day == 1 ? "" : "s")}</span></div>");
        sb.Append($"<div class=\"pair\"><span class=\"k\">target daily spend</span><span class=\"v cost\">{Money(dailyTarget)}</span> <span class=\"muted\">{Money(monthlyTargetSpendUsd)} monthly target / {daysInMonth} days</span></div>");
    }

    private static void AppendProviderButtons(StringBuilder sb, string basePath, string? activeProvider)
    {
        sb.Append("<span class=\"nav-right\">");
        foreach (var (label, value) in new[] { ("All", (string?)null), ("Vertex", "vertex"), ("Anthropic", "anthropic") })
        {
            var isActive = activeProvider == value;
            var href = value is null ? basePath : $"{basePath}?provider={value}";
            var cls = isActive ? "prov-btn prov-active" : "prov-btn";
            sb.Append($"<a href=\"{href}\" class=\"{cls}\">{label}</a>");
        }
        sb.Append("</span>");
    }

    private static DateOnly? SnapshotDate(StatsSnapshot s) =>
        DateOnly.TryParseExact(s.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    private static PricingCatalog.RateBlock? ComputeBlendedRates(IReadOnlyDictionary<string, ModelSnapshot> models, PricingCatalog pricing, DateOnly? onDate)
    {
        // Models without pricing entries bias blended cost downward; acceptable since only count-tokens lacks pricing.
        long totIn = 0, totRead = 0, tot5m = 0, tot1h = 0, totOut = 0;
        decimal weightedIn = 0, weightedRead = 0, weighted5m = 0, weighted1h = 0, weightedOut = 0;

        foreach (var (key, m) in models)
        {
            var modelName = m.Model ?? key;
            totIn += m.InputUncached;
            totRead += m.CacheRead;
            tot5m += m.CacheCreation5m;
            tot1h += m.CacheCreation1h;
            totOut += m.Output;

            if (!pricing.TryGetRates(modelName, out var r, onDate)) continue;
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

    private static void AppendFailureBanners(
        StringBuilder sb,
        IReadOnlyDictionary<PromptFailureMode, long> failures,
        IReadOnlyDictionary<PromptFailureMode, long> acknowledged,
        IReadOnlyList<string> unknownPaths)
    {
        foreach (var (mode, count) in failures)
        {
            var unacknowledged = count - acknowledged.GetValueOrDefault(mode);
            if (unacknowledged <= 0) continue;
            var n = N0(unacknowledged);
            var req = unacknowledged == 1 ? "request" : "requests";
            string msg;
            if (mode == PromptFailureMode.UnknownPath)
            {
                var paths = string.Join("", unknownPaths.Select(p => $"<li><code>{Esc(p)}</code></li>"));
                msg = $"{n} {req} forwarded without prompt injection &mdash; path not recognised as a model API call.<ul style=\"margin:.25rem 0 0 1.25rem;\">{paths}</ul>";
            }
            else
            {
                msg = mode switch
                {
                    PromptFailureMode.PromptNotConfigured   => $"{n} {req} passed through unmodified &mdash; <code>Proxy:PromptFilePath</code> is not configured in <code>appsettings.json</code>.",
                    PromptFailureMode.PromptFileNotFound    => $"{n} {req} passed through unmodified &mdash; prompt file not found on disk. Check <code>Proxy:PromptFilePath</code> in <code>appsettings.json</code>.",
                    PromptFailureMode.PromptFileReadError   => $"{n} {req} passed through unmodified &mdash; prompt file became unreadable during the request (I/O error). Check file permissions and disk health.",
                    PromptFailureMode.NoBulkBlock           => $"{n} {req} passed through unmodified &mdash; no bulk system block found. Claude Code&rsquo;s request shape may have changed.",
                    PromptFailureMode.NoBoundaryMarker      => $"{n} {req} passed through unmodified &mdash; bulk block found but no dynamic boundary marker present. Claude Code&rsquo;s prompt structure may have changed.",
                    PromptFailureMode.IdentityNotRecognized => $"{n} {req} passed through unchanged because no recognized Claude Code identity block was present. If this was normal Claude Code traffic, update <code>KnownIdentityPrefixes</code> in <code>PromptRewriter.cs</code>.",
                    PromptFailureMode.UnhandledException    => $"{n} {req} passed through unmodified due to an unexpected exception during prompt rewrite. Check the service logs.",
                    _                                       => $"{n} {req} encountered rewrite failure: {Esc(mode.ToString())}.",
                };
            }
            var dismiss = $"<form method=\"post\" action=\"/stats/dismiss/{Esc(mode.ToString())}\" style=\"display:contents;\"><button type=\"submit\" class=\"dismiss-btn\">Dismiss</button></form>";
            sb.Append($"<div class=\"failure-warn\"><span>&#9888; {msg}</span>{dismiss}</div>");
        }
    }

    // Claude logo (24x24 path) at translate(15,3); content x∈[15,39] y∈[8,23]; arrows x∈[0,54]; viewBox adds stroke-width/2 padding
    private const string HeaderIconSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="-1 7 56 17" fill="none" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="0" y1="15" x2="10" y2="15" stroke="#D97757"/><polyline points="7,12 10,15 7,18" stroke="#D97757"/><g transform="translate(15,3)"><path clip-rule="evenodd" fill-rule="evenodd" fill="#D97757" d="M20.998 10.949H24v3.102h-3v3.028h-1.487V20H18v-2.921h-1.487V20H15v-2.921H9V20H7.488v-2.921H6V20H4.487v-2.921H3V14.05H0V10.95h3V5h17.998v5.949zM6 10.949h1.488V8.102H6v2.847zm10.51 0H18V8.102h-1.49v2.847z"/></g><line x1="44" y1="15" x2="54" y2="15" stroke="#D97757"/><polyline points="51,12 54,15 51,18" stroke="#D97757"/></svg>""";
}
