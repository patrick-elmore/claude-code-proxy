using ClaudeCodeProxy.Presentation.Stats;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy.Pages.Stats;

public sealed class WeekModel(
    StatsTracker tracker,
    SessionMetadataStore metadata,
    StatsPageViewModelFactory pageBuilder,
    IOptions<StatsOptions> statsOptions)
    : StatsPageModelBase(tracker, metadata, pageBuilder, statsOptions)
{
    public void OnGet() => View = PageBuilder.BuildRange(
        LoadRange(7), "Last 7 days", "/Stats/Week", SelectedProvider, Metadata.GetSnapshot());
}
