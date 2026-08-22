using ClaudeCodeProxy.Presentation.Stats;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy.Pages.Stats;

public sealed class MonthModel(
    StatsTracker tracker,
    SessionMetadataStore metadata,
    StatsPageViewModelFactory pageBuilder,
    IOptions<StatsOptions> statsOptions)
    : StatsPageModelBase(tracker, metadata, pageBuilder, statsOptions)
{
    public void OnGet() => View = PageBuilder.BuildRange(
        LoadRange(30), "Last 30 days", "/Stats/Month", SelectedProvider, Metadata.GetSnapshot());
}
