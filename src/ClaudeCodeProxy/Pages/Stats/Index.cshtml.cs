using ClaudeCodeProxy.Presentation.Stats;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy.Pages.Stats;

public sealed class IndexModel(
    StatsTracker tracker,
    SessionMetadataStore metadata,
    StatsPageViewModelFactory pageBuilder,
    IOptions<StatsOptions> statsOptions)
    : StatsPageModelBase(tracker, metadata, pageBuilder, statsOptions)
{
    public void OnGet()
    {
        var today = Tracker.ComputeCurrentStatsDay();
        var (previous, next) = StatsNavigation.AdjacentDates(Tracker.GetAvailableDates(), today, today);
        View = PageBuilder.BuildDaily(Tracker.GetSnapshot(), previous, next, SelectedProvider,
            LoadCurrentMonth(today), StatsOptions.MonthlyTargetSpendUsd, Metadata.GetSnapshot());
    }
}
