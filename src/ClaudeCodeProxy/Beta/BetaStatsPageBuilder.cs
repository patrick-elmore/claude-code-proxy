using System.Globalization;

namespace ClaudeCodeProxy.Beta;

public sealed class BetaStatsPageBuilder(PricingCatalog pricing)
{
    public static string? NormalizeProvider(string? value)
    {
        var normalized = value?.ToLowerInvariant();
        return normalized is "vertex" or "anthropic" ? normalized : null;
    }

    public BetaStatsPageViewModel BuildDaily(
        StatsSnapshot snapshot,
        DateOnly? previousDate,
        DateOnly? nextDate,
        string? provider,
        IReadOnlyList<StatsSnapshot>? currentMonthSnapshots,
        decimal monthlyTargetSpendUsd,
        SessionMetadataSnapshot? sessionMetadata)
    {
        var filteredModels = FilteredModels(snapshot, provider);
        var snapshotDate = SnapshotDate(snapshot);
        var modelRows = new List<BetaModelRowViewModel>();
        long requests = 0, failed = 0, uncached = 0, read = 0, cc5m = 0, cc1h = 0, output = 0;
        decimal totalCost = 0m;

        foreach (var (key, model) in filteredModels)
        {
            var modelName = model.Model ?? key;
            requests += model.Requests;
            failed += model.Failed;
            uncached += model.InputUncached;
            read += model.CacheRead;
            cc5m += model.CacheCreation5m;
            cc1h += model.CacheCreation1h;
            output += model.Output;
            var cost = pricing.CalculateCostUsd(modelName, model.InputUncached, model.CacheRead,
                model.CacheCreation5m, model.CacheCreation1h, model.Output, snapshotDate);
            totalCost += cost;
            modelRows.Add(new BetaModelRowViewModel(modelName, model.Provider, model.Requests, model.Failed,
                model.HitRatePct, model.AvgDurationMs, model.CacheRead, model.CacheCreation,
                model.InputUncached, model.Output, cost));
        }

        modelRows = modelRows.OrderBy(row => row.Model, StringComparer.Ordinal)
            .ThenBy(row => row.Provider, StringComparer.Ordinal)
            .ToList();

        var input = uncached + read + cc5m + cc1h;
        var hitRate = input > 0 ? 100.0 * read / input : 0;
        var isHistorical = snapshot.UptimeSeconds < 0;
        var subtitle = isHistorical
            ? $"stats for {snapshot.Date} · historical"
            : $"stats for {snapshot.Date} · uptime: {BetaStatsDisplay.Uptime(snapshot.UptimeSeconds)}";

        var hours = BuildHourlyRows(snapshot, filteredModels, provider, snapshotDate);
        var endpoints = snapshot.ByEndpoint
            .Where(pair => provider is null || pair.Value.Provider == provider)
            .OrderBy(pair => pair.Value.Provider, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BetaEndpointRowViewModel(pair.Key, pair.Value.Provider,
                pair.Value.Requests, pair.Value.Failed))
            .ToList();

        var monthSpend = currentMonthSnapshots is not null && snapshotDate is { } currentDate
            ? BuildMonthSpend(currentMonthSnapshots, provider, currentDate, monthlyTargetSpendUsd)
            : null;

        return new BetaStatsPageViewModel(
            $"Claude Code Proxy — {snapshot.Date}",
            subtitle,
            provider is null ? "Today" : $"Today ({provider})",
            isHistorical ? "/Beta/Stats/Day" : "/Beta/Stats/Index",
            isHistorical ? snapshot.Date : null,
            provider,
            previousDate,
            nextDate,
            isHistorical,
            IsRange: false,
            new BetaSummaryViewModel(requests, failed, snapshot.InjectedRequests,
                snapshot.PassedThroughRequests, hitRate, input, snapshot.AvgDurationMs,
                totalCost, provider is not null),
            new BetaTokenTotalsViewModel(read, cc5m + cc1h, uncached, output),
            BuildFailureBanners(snapshot.RewriteFailures, snapshot.AcknowledgedFailures, snapshot.UnknownPaths),
            modelRows,
            [],
            BuildSessionRows([(snapshot, snapshotDate)], provider, sessionMetadata),
            endpoints,
            hours,
            monthSpend);
    }

    public BetaStatsPageViewModel BuildRange(
        IReadOnlyList<StatsSnapshot> snapshots,
        string title,
        string currentPage,
        string? provider,
        SessionMetadataSnapshot? sessionMetadata)
    {
        long requests = 0, failed = 0, injected = 0, passedThrough = 0;
        long uncached = 0, read = 0, cc5m = 0, cc1h = 0, output = 0;
        decimal totalCost = 0m;
        var mergedModels = new Dictionary<string, MutableModel>(StringComparer.Ordinal);
        var mergedEndpoints = new Dictionary<string, MutableEndpoint>(StringComparer.Ordinal);

        foreach (var snapshot in snapshots)
        {
            injected += snapshot.InjectedRequests;
            passedThrough += snapshot.PassedThroughRequests;
            var snapshotDate = SnapshotDate(snapshot);
            foreach (var (key, model) in FilteredModels(snapshot, provider))
            {
                var modelName = model.Model ?? key;
                requests += model.Requests;
                failed += model.Failed;
                uncached += model.InputUncached;
                read += model.CacheRead;
                cc5m += model.CacheCreation5m;
                cc1h += model.CacheCreation1h;
                output += model.Output;
                var cost = pricing.CalculateCostUsd(modelName, model.InputUncached, model.CacheRead,
                    model.CacheCreation5m, model.CacheCreation1h, model.Output, snapshotDate);
                totalCost += cost;

                if (!mergedModels.TryGetValue(key, out var merged))
                    mergedModels[key] = merged = new MutableModel(modelName, model.Provider);
                merged.Add(model, cost);
            }

            foreach (var (endpoint, stats) in snapshot.ByEndpoint)
            {
                if (provider is not null && stats.Provider != provider) continue;
                if (!mergedEndpoints.TryGetValue(endpoint, out var merged))
                    mergedEndpoints[endpoint] = merged = new MutableEndpoint(stats.Provider);
                merged.Add(stats);
            }
        }

        var input = uncached + read + cc5m + cc1h;
        var hitRate = input > 0 ? 100.0 * read / input : 0;
        var failures = Enum.GetValues<PromptFailureMode>()
            .ToDictionary(mode => mode, mode => snapshots.Sum(s => s.RewriteFailures.GetValueOrDefault(mode)));
        var acknowledged = Enum.GetValues<PromptFailureMode>()
            .ToDictionary(mode => mode, mode => snapshots.Sum(s => s.AcknowledgedFailures.GetValueOrDefault(mode)));
        var unknownPaths = snapshots.SelectMany(s => s.UnknownPaths).Distinct(StringComparer.Ordinal).ToList();

        var models = mergedModels.Values
            .OrderBy(row => row.Model, StringComparer.Ordinal)
            .ThenBy(row => row.Provider, StringComparer.Ordinal)
            .Select(row => row.ToViewModel())
            .ToList();
        var endpoints = mergedEndpoints
            .OrderBy(pair => pair.Value.Provider, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BetaEndpointRowViewModel(pair.Key, pair.Value.Provider,
                pair.Value.Requests, pair.Value.Failed))
            .ToList();

        var days = snapshots.OrderByDescending(s => s.Date, StringComparer.Ordinal)
            .Select(snapshot => BuildDayRow(snapshot, provider))
            .ToList();

        return new BetaStatsPageViewModel(
            $"Claude Code Proxy — {title}",
            $"{title} · {snapshots.Count} day{(snapshots.Count == 1 ? "" : "s")} of data",
            "Totals",
            currentPage,
            null,
            provider,
            null,
            null,
            IsHistorical: false,
            IsRange: true,
            new BetaSummaryViewModel(requests, failed, injected, passedThrough, hitRate, input,
                AvgDurationMs: null, totalCost, provider is not null),
            new BetaTokenTotalsViewModel(read, cc5m + cc1h, uncached, output),
            BuildFailureBanners(failures, acknowledged, unknownPaths),
            models,
            days,
            BuildSessionRows(snapshots.Select(s => (s, SnapshotDate(s))), provider, sessionMetadata),
            endpoints,
            [],
            null);
    }

    private BetaDayRowViewModel BuildDayRow(StatsSnapshot snapshot, string? provider)
    {
        long requests = 0, failed = 0, uncached = 0, read = 0, cc5m = 0, cc1h = 0, output = 0;
        decimal cost = 0m;
        var snapshotDate = SnapshotDate(snapshot);
        foreach (var (key, model) in FilteredModels(snapshot, provider))
        {
            var modelName = model.Model ?? key;
            requests += model.Requests;
            failed += model.Failed;
            uncached += model.InputUncached;
            read += model.CacheRead;
            cc5m += model.CacheCreation5m;
            cc1h += model.CacheCreation1h;
            output += model.Output;
            cost += pricing.CalculateCostUsd(modelName, model.InputUncached, model.CacheRead,
                model.CacheCreation5m, model.CacheCreation1h, model.Output, snapshotDate);
        }

        var input = uncached + read + cc5m + cc1h;
        return new BetaDayRowViewModel(snapshot.Date, snapshot.UptimeSeconds >= 0, requests, failed,
            input > 0 ? 100.0 * read / input : 0, snapshot.AvgDurationMs, input, output, cost);
    }

    private IReadOnlyList<BetaHourlyRowViewModel> BuildHourlyRows(
        StatsSnapshot snapshot,
        IReadOnlyDictionary<string, ModelSnapshot> filteredModels,
        string? provider,
        DateOnly? snapshotDate)
    {
        var blended = ComputeBlendedRates(filteredModels, snapshotDate);
        var rows = new List<BetaHourlyRowViewModel>();
        foreach (var hour in snapshot.Hourly)
        {
            var providerHour = provider is not null ? hour.ByProvider?.GetValueOrDefault(provider) : null;
            var requests = providerHour?.Requests ?? hour.Requests;
            var failed = providerHour?.FailedCount ?? hour.FailedCount;
            var uncached = providerHour?.InputUncached ?? hour.InputUncached;
            var read = providerHour?.CacheRead ?? hour.CacheRead;
            var cc5m = providerHour?.CacheCreation5m ?? hour.CacheCreation5m;
            var cc1h = providerHour?.CacheCreation1h ?? hour.CacheCreation1h;
            var output = providerHour?.Output ?? hour.Output;
            var hitRate = providerHour?.HitRatePct ?? hour.HitRatePct;
            var avgMs = providerHour?.AvgDurationMs ?? hour.AvgDurationMs;
            var cost = blended.HasValue
                ? (uncached * blended.Value.Input
                   + read * blended.Value.CacheHit
                   + cc5m * blended.Value.CacheWrite5m
                   + cc1h * blended.Value.CacheWrite1h
                   + output * blended.Value.Output) / 1_000_000m
                : 0m;
            rows.Add(new BetaHourlyRowViewModel(
                hour.Hour.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                requests, failed, hitRate, avgMs, read, cc5m + cc1h, uncached, output, cost));
        }
        return rows;
    }

    private BetaMonthSpendViewModel BuildMonthSpend(
        IReadOnlyList<StatsSnapshot> snapshots,
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
                monthToDateCost += pricing.CalculateCostUsd(model.Model ?? key, model.InputUncached,
                    model.CacheRead, model.CacheCreation5m, model.CacheCreation1h, model.Output, snapshotDate);
            }
        }

        var daysInMonth = DateTime.DaysInMonth(currentDate.Year, currentDate.Month);
        return new BetaMonthSpendViewModel(
            currentDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture) + " daily spend",
            monthToDateCost / currentDate.Day,
            monthlyTargetSpendUsd / daysInMonth,
            monthlyTargetSpendUsd,
            currentDate.Day,
            daysInMonth);
    }

    private IReadOnlyList<BetaSessionRowViewModel> BuildSessionRows(
        IEnumerable<(StatsSnapshot Snapshot, DateOnly? Date)> snapshots,
        string? provider,
        SessionMetadataSnapshot? metadata)
    {
        var sessions = new Dictionary<string, MutableSession>(StringComparer.Ordinal);
        foreach (var (snapshot, date) in snapshots)
        {
            foreach (var session in snapshot.Sessions ?? [])
            {
                var sessionKey = session.SessionId ?? "";
                if (!sessions.TryGetValue(sessionKey, out var mergedSession))
                    sessions[sessionKey] = mergedSession = new MutableSession(session.SessionId);

                foreach (var agent in session.Agents)
                {
                    var metrics = FilteredAgentMetrics(agent, provider);
                    if (!metrics.HasData) continue;
                    var agentKey = agent.AgentId ?? "";
                    if (!mergedSession.Agents.TryGetValue(agentKey, out var mergedAgent))
                        mergedSession.Agents[agentKey] = mergedAgent = new MutableAgent(agent.AgentId, agent.ParentAgentId);
                    else if (mergedAgent.ParentAgentId is null && agent.ParentAgentId is not null)
                        mergedAgent.ParentAgentId = agent.ParentAgentId;

                    var cost = agent.ByModel.Values
                        .Where(model => provider is null || model.Provider == provider)
                        .Sum(model => pricing.CalculateCostUsd(model.Model, model.InputUncached, model.CacheRead,
                            model.CacheCreation5m, model.CacheCreation1h, model.Output, date));
                    mergedAgent.Add(metrics, cost);
                }
            }
        }

        var output = new List<BetaSessionRowViewModel>();
        foreach (var session in sessions.Values
            .Where(session => session.Metrics.HasData)
            .OrderBy(session => session.SessionId is null ? 1 : 0)
            .ThenByDescending(session => session.Cost)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal))
        {
            SessionMetadataSessionSnapshot? sessionMetadata = null;
            if (session.SessionId is not null)
                metadata?.Sessions.TryGetValue(session.SessionId, out sessionMetadata);

            var label = session.SessionId is null
                ? "Unattributed"
                : SessionLabel(session.SessionId, sessionMetadata);
            output.Add(ToSessionViewModel(session, label));
            if (session.SessionId is not null)
                AppendAgentRows(output, session, sessionMetadata?.Agents);
        }
        return output;
    }

    private static BetaSessionRowViewModel ToSessionViewModel(MutableSession session, string label)
    {
        var metrics = session.Metrics;
        return new BetaSessionRowViewModel(BetaSessionRowKind.Session, 0, label, session.SessionId,
            null, false, session.SessionId is null, metrics.Requests, metrics.Failed,
            metrics.InputTokens, metrics.Output, session.Cost);
    }

    private static void AppendAgentRows(
        ICollection<BetaSessionRowViewModel> output,
        MutableSession session,
        IReadOnlyDictionary<string, AgentMetadataSnapshot>? metadata)
    {
        var agents = session.Agents.Values.ToList();
        var byId = agents.Where(agent => agent.AgentId is not null)
            .ToDictionary(agent => agent.AgentId!, StringComparer.Ordinal);
        var children = agents
            .Where(agent => agent.AgentId is not null && agent.ParentAgentId is not null
                && byId.ContainsKey(agent.ParentAgentId))
            .GroupBy(agent => agent.ParentAgentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(agent => agent.AgentId, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        if (session.Agents.TryGetValue("", out var mainAgent))
            AppendAgentBranch(output, mainAgent, 1, false, children, visited, metadata);

        foreach (var agent in agents
            .Where(agent => agent.AgentId is not null
                && (agent.ParentAgentId is null || !byId.ContainsKey(agent.ParentAgentId)))
            .OrderBy(agent => agent.AgentId, StringComparer.Ordinal))
        {
            AppendAgentBranch(output, agent, 1, agent.ParentAgentId is not null, children, visited, metadata);
        }

        foreach (var agent in agents.Where(agent => agent.AgentId is not null)
            .OrderBy(agent => agent.AgentId, StringComparer.Ordinal))
        {
            if (!visited.Contains(agent.AgentId!))
                AppendAgentBranch(output, agent, 1, false, children, visited, metadata);
        }
    }

    private static void AppendAgentBranch(
        ICollection<BetaSessionRowViewModel> output,
        MutableAgent agent,
        int depth,
        bool parentMissing,
        IReadOnlyDictionary<string, List<MutableAgent>> children,
        ISet<string> visited,
        IReadOnlyDictionary<string, AgentMetadataSnapshot>? metadata)
    {
        if (agent.AgentId is not null && !visited.Add(agent.AgentId)) return;
        var metrics = agent.Metrics;
        var kind = agent.AgentId is null ? BetaSessionRowKind.MainAgent : BetaSessionRowKind.Agent;
        string label;
        if (agent.AgentId is null)
        {
            label = "Main agent";
        }
        else
        {
            var agentType = metadata?.TryGetValue(agent.AgentId, out var value) == true ? value.AgentType : null;
            label = string.IsNullOrWhiteSpace(agentType) ? "Agent" : agentType;
        }

        output.Add(new BetaSessionRowViewModel(kind, depth, label, agent.AgentId,
            agent.ParentAgentId, parentMissing, false, metrics.Requests, metrics.Failed,
            metrics.InputTokens, metrics.Output, agent.Cost));

        if (agent.AgentId is not null && children.TryGetValue(agent.AgentId, out var childAgents))
        {
            foreach (var child in childAgents)
                AppendAgentBranch(output, child, depth + 1, false, children, visited, metadata);
        }
    }

    private static string SessionLabel(string sessionId, SessionMetadataSessionSnapshot? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.DisplayName)) return metadata.DisplayName;
        if (!string.IsNullOrWhiteSpace(metadata?.LastCwd))
        {
            var trimmed = metadata.LastCwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(leaf))
                return $"{leaf} ({sessionId[..Math.Min(sessionId.Length, 8)]})";
        }
        return "Session";
    }

    private static SessionMetrics FilteredAgentMetrics(AgentSnapshot agent, string? provider)
    {
        if (provider is null)
            return new SessionMetrics(agent.Requests, agent.Failed, agent.Totals);
        return agent.ByProvider.TryGetValue(provider, out var totals)
            ? new SessionMetrics(totals.Requests, totals.Failed,
                new TokenTotals(totals.InputUncached, totals.CacheRead, totals.CacheCreation5m,
                    totals.CacheCreation1h, totals.Output))
            : SessionMetrics.Empty;
    }

    private static IReadOnlyList<BetaFailureBannerViewModel> BuildFailureBanners(
        IReadOnlyDictionary<PromptFailureMode, long> failures,
        IReadOnlyDictionary<PromptFailureMode, long> acknowledged,
        IReadOnlyList<string> unknownPaths) =>
        failures
            .Select(pair => new BetaFailureBannerViewModel(pair.Key,
                pair.Value - acknowledged.GetValueOrDefault(pair.Key),
                pair.Key == PromptFailureMode.UnknownPath ? unknownPaths : []))
            .Where(banner => banner.Count > 0)
            .ToList();

    private static IReadOnlyDictionary<string, ModelSnapshot> FilteredModels(StatsSnapshot snapshot, string? provider) =>
        provider is null
            ? snapshot.ByModel
            : snapshot.ByModel.Where(pair => pair.Value.Provider == provider)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static DateOnly? SnapshotDate(StatsSnapshot snapshot) =>
        DateOnly.TryParseExact(snapshot.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    private PricingCatalog.RateBlock? ComputeBlendedRates(
        IReadOnlyDictionary<string, ModelSnapshot> models,
        DateOnly? date)
    {
        long totalInput = 0, totalRead = 0, total5m = 0, total1h = 0, totalOutput = 0;
        decimal input = 0, read = 0, write5m = 0, write1h = 0, output = 0;
        foreach (var (key, model) in models)
        {
            totalInput += model.InputUncached;
            totalRead += model.CacheRead;
            total5m += model.CacheCreation5m;
            total1h += model.CacheCreation1h;
            totalOutput += model.Output;
            if (!pricing.TryGetRates(model.Model ?? key, out var rates, date)) continue;
            input += model.InputUncached * rates.Input;
            read += model.CacheRead * rates.CacheHit;
            write5m += model.CacheCreation5m * rates.CacheWrite5m;
            write1h += model.CacheCreation1h * rates.CacheWrite1h;
            output += model.Output * rates.Output;
        }

        if (totalInput + totalRead + total5m + total1h + totalOutput == 0) return null;
        return new PricingCatalog.RateBlock(
            totalInput > 0 ? input / totalInput : 0,
            totalOutput > 0 ? output / totalOutput : 0,
            total5m > 0 ? write5m / total5m : 0,
            total1h > 0 ? write1h / total1h : 0,
            totalRead > 0 ? read / totalRead : 0);
    }

    private sealed class MutableModel(string model, string? provider)
    {
        public string Model { get; } = model;
        public string? Provider { get; } = provider;
        public long Requests { get; private set; }
        public long Failed { get; private set; }
        public long InputUncached { get; private set; }
        public long CacheRead { get; private set; }
        public long CacheCreation { get; private set; }
        public long Output { get; private set; }
        public decimal Cost { get; private set; }

        public void Add(ModelSnapshot value, decimal cost)
        {
            Requests += value.Requests;
            Failed += value.Failed;
            InputUncached += value.InputUncached;
            CacheRead += value.CacheRead;
            CacheCreation += value.CacheCreation;
            Output += value.Output;
            Cost += cost;
        }

        public BetaModelRowViewModel ToViewModel()
        {
            var input = InputUncached + CacheRead + CacheCreation;
            var hitRate = input > 0 ? 100.0 * CacheRead / input : 0;
            return new BetaModelRowViewModel(Model, Provider, Requests, Failed, hitRate, null,
                CacheRead, CacheCreation, InputUncached, Output, Cost);
        }
    }

    private sealed class MutableEndpoint(string? provider)
    {
        public string? Provider { get; private set; } = provider;
        public long Requests { get; private set; }
        public long Failed { get; private set; }

        public void Add(EndpointSnapshot value)
        {
            Requests += value.Requests;
            Failed += value.Failed;
            Provider ??= value.Provider;
        }
    }

    private sealed class MutableSession(string? sessionId)
    {
        public string? SessionId { get; } = sessionId;
        public Dictionary<string, MutableAgent> Agents { get; } = new(StringComparer.Ordinal);
        public SessionMetrics Metrics => Agents.Values.Aggregate(SessionMetrics.Empty, (sum, agent) => sum + agent.Metrics);
        public decimal Cost => Agents.Values.Sum(agent => agent.Cost);
    }

    private sealed class MutableAgent(string? agentId, string? parentAgentId)
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
        public bool HasData => Requests != 0 || Failed != 0 || InputTokens != 0 || Output != 0;
        public long InputTokens => Totals.InputUncached + Totals.CacheRead + Totals.CacheCreation;
        public long Output => Totals.Output;

        public static SessionMetrics operator +(SessionMetrics left, SessionMetrics right) =>
            new(left.Requests + right.Requests, left.Failed + right.Failed,
                new TokenTotals(left.Totals.InputUncached + right.Totals.InputUncached,
                    left.Totals.CacheRead + right.Totals.CacheRead,
                    left.Totals.CacheCreation5m + right.Totals.CacheCreation5m,
                    left.Totals.CacheCreation1h + right.Totals.CacheCreation1h,
                    left.Totals.Output + right.Totals.Output));
    }
}
