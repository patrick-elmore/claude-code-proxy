namespace ClaudeCodeProxy.Beta;

public sealed record BetaStatsPageViewModel(
    string DocumentTitle,
    string HeaderSubtitle,
    string SummaryTitle,
    string CurrentPage,
    string? CurrentDate,
    string? Provider,
    DateOnly? PreviousDate,
    DateOnly? NextDate,
    bool IsHistorical,
    bool IsRange,
    BetaSummaryViewModel Summary,
    BetaTokenTotalsViewModel Tokens,
    IReadOnlyList<BetaFailureBannerViewModel> Failures,
    IReadOnlyList<BetaModelRowViewModel> Models,
    IReadOnlyList<BetaDayRowViewModel> Days,
    IReadOnlyList<BetaSessionRowViewModel> Sessions,
    IReadOnlyList<BetaEndpointRowViewModel> Endpoints,
    IReadOnlyList<BetaHourlyRowViewModel> Hours,
    BetaMonthSpendViewModel? MonthSpend);

public sealed record BetaSummaryViewModel(
    long Requests,
    long Failed,
    long InjectedRequests,
    long PassedThroughRequests,
    double HitRatePct,
    long InputTokens,
    double? AvgDurationMs,
    decimal TotalCost,
    bool PromptCountsAreGlobal);

public sealed record BetaTokenTotalsViewModel(
    long CacheRead,
    long CacheCreation,
    long InputUncached,
    long Output);

public sealed record BetaFailureBannerViewModel(
    PromptFailureMode Mode,
    long Count,
    IReadOnlyList<string> UnknownPaths);

public sealed record BetaModelRowViewModel(
    string Model,
    string? Provider,
    long Requests,
    long Failed,
    double HitRatePct,
    double? AvgDurationMs,
    long CacheRead,
    long CacheCreation,
    long InputUncached,
    long Output,
    decimal Cost);

public sealed record BetaDayRowViewModel(
    string Date,
    bool IsToday,
    long Requests,
    long Failed,
    double HitRatePct,
    double AvgDurationMs,
    long InputTokens,
    long Output,
    decimal Cost);

public enum BetaSessionRowKind
{
    Session,
    MainAgent,
    Agent,
}

public sealed record BetaSessionRowViewModel(
    BetaSessionRowKind Kind,
    int Depth,
    string Label,
    string? Id,
    string? ParentAgentId,
    bool ParentMissing,
    bool IsUnattributed,
    long Requests,
    long Failed,
    long InputTokens,
    long Output,
    decimal Cost);

public sealed record BetaEndpointRowViewModel(
    string Endpoint,
    string? Provider,
    long Requests,
    long Failed);

public sealed record BetaHourlyRowViewModel(
    string Hour,
    long Requests,
    long Failed,
    double HitRatePct,
    double AvgDurationMs,
    long CacheRead,
    long CacheCreation,
    long InputUncached,
    long Output,
    decimal Cost);

public sealed record BetaMonthSpendViewModel(
    string Title,
    decimal DailyAverage,
    decimal DailyTarget,
    decimal MonthlyTarget,
    int ElapsedDays,
    int DaysInMonth);

public sealed record BetaApplicationVersion(string Value);
