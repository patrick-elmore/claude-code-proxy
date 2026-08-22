using System.Globalization;
using ClaudeCodeProxy.Presentation.Stats;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy.Pages.Stats;

public sealed class DayModel(
    StatsTracker tracker,
    SessionMetadataStore metadata,
    StatsPageViewModelFactory pageBuilder,
    IOptions<StatsOptions> statsOptions)
    : StatsPageModelBase(tracker, metadata, pageBuilder, statsOptions)
{
    public IActionResult OnGet(string date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var selectedDate))
            return BadRequest("Date must be yyyy-MM-dd.");

        var snapshot = Tracker.GetSnapshotForDate(selectedDate);
        if (snapshot is null) return NotFound($"No stats for {date}.");

        var today = Tracker.ComputeCurrentStatsDay();
        var (previous, next) = StatsNavigation.AdjacentDates(
            Tracker.GetAvailableDates(), selectedDate, today);
        View = PageBuilder.BuildDaily(snapshot, previous, next, SelectedProvider,
            currentMonthSnapshots: null, StatsOptions.MonthlyTargetSpendUsd, Metadata.GetSnapshot());
        return Page();
    }
}
