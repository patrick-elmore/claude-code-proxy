namespace ClaudeCodeProxy.Presentation.Stats;

public sealed record StatsPageViewModel(
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
    SummaryViewModel Summary,
    TokenTotalsViewModel Tokens,
    IReadOnlyList<FailureBannerViewModel> Failures,
    IReadOnlyList<ModelRowViewModel> Models,
    IReadOnlyList<DayRowViewModel> Days,
    IReadOnlyList<SessionRowViewModel> Sessions,
    IReadOnlyList<EndpointRowViewModel> Endpoints,
    IReadOnlyList<HourlyRowViewModel> Hours,
    MonthSpendViewModel? MonthSpend);

public sealed record SummaryViewModel(
    long Requests,
    long Failed,
    long InjectedRequests,
    long PassedThroughRequests,
    double HitRatePct,
    long InputTokens,
    double? AvgDurationMs,
    decimal TotalCost,
    bool PromptCountsAreGlobal);

public sealed record TokenTotalsViewModel(
    long CacheRead,
    long CacheCreation,
    long InputUncached,
    long Output);

public sealed record FailureBannerViewModel(
    PromptFailureMode Mode,
    long Count,
    IReadOnlyList<string> UnknownPaths);

public sealed record ModelRowViewModel(
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

public sealed record DayRowViewModel(
    string Date,
    bool IsToday,
    long Requests,
    long Failed,
    double HitRatePct,
    double AvgDurationMs,
    long InputTokens,
    long Output,
    decimal Cost);

public enum SessionRowKind
{
    Session,
    MainAgent,
    Agent,
}

public sealed record SessionRowViewModel(
    SessionRowKind Kind,
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

public sealed record EndpointRowViewModel(
    string Endpoint,
    string? Provider,
    long Requests,
    long Failed);

public sealed record HourlyRowViewModel(
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

public sealed record MonthSpendViewModel(
    string Title,
    decimal DailyAverage,
    decimal DailyTarget,
    decimal MonthlyTarget,
    int ElapsedDays,
    int DaysInMonth);

public sealed record ApplicationVersion(string Value);
