using ClaudeCodeProxy.Presentation.Stats;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy.Pages.Stats;

public abstract class StatsPageModelBase(
    StatsTracker tracker,
    SessionMetadataStore metadata,
    StatsPageViewModelFactory pageBuilder,
    IOptions<StatsOptions> statsOptions) : PageModel
{
    protected StatsTracker Tracker { get; } = tracker;
    protected SessionMetadataStore Metadata { get; } = metadata;
    protected StatsPageViewModelFactory PageBuilder { get; } = pageBuilder;
    protected StatsOptions StatsOptions { get; } = statsOptions.Value;

    public StatsPageViewModel View { get; protected set; } = null!;

    protected string? SelectedProvider =>
        StatsPageViewModelFactory.NormalizeProvider(Request.Query["provider"].FirstOrDefault());

    protected IReadOnlyList<StatsSnapshot> LoadRange(int days)
    {
        var today = Tracker.ComputeCurrentStatsDay();
        var snapshots = new List<StatsSnapshot>(days);
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var snapshot = Tracker.GetSnapshotForDate(today.AddDays(-offset));
            if (snapshot is not null) snapshots.Add(snapshot);
        }
        return snapshots;
    }

    protected IReadOnlyList<StatsSnapshot> LoadCurrentMonth(DateOnly today)
    {
        var firstDay = new DateOnly(today.Year, today.Month, 1);
        var snapshots = new List<StatsSnapshot>(today.Day);
        for (var date = firstDay; date <= today; date = date.AddDays(1))
        {
            var snapshot = Tracker.GetSnapshotForDate(date);
            if (snapshot is not null) snapshots.Add(snapshot);
        }
        return snapshots;
    }
}
