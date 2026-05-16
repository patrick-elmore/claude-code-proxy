using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class StatsTrackerTests : IDisposable
{
    private readonly string _statsDir;

    public StatsTrackerTests()
    {
        _statsDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_statsDir);
    }

    public void Dispose() => Directory.Delete(_statsDir, recursive: true);

    private StatsTracker Build() =>
        new(new OptionsWrapper<StatsOptions>(new StatsOptions { OutputPath = _statsDir }),
            NullLogger<StatsTracker>.Instance);

    [Fact]
    public void AcknowledgeFailureMode_SetsWatermark_BannerSuppressed()
    {
        var tracker = Build();
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.PromptFileNotFound));
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.PromptFileNotFound));

        tracker.AcknowledgeFailureMode(PromptFailureMode.PromptFileNotFound);

        var snapshot = tracker.GetSnapshot();
        snapshot.RewriteFailures[PromptFailureMode.PromptFileNotFound].ShouldBe(2);
        snapshot.AcknowledgedFailures[PromptFailureMode.PromptFileNotFound].ShouldBe(2);
        // Unacknowledged delta is zero — banner should not show
        (snapshot.RewriteFailures[PromptFailureMode.PromptFileNotFound]
            - snapshot.AcknowledgedFailures[PromptFailureMode.PromptFileNotFound]).ShouldBe(0);
    }

    [Fact]
    public void AcknowledgeFailureMode_NewFailureAfterAck_RetriggersBanner()
    {
        var tracker = Build();
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.PromptFileNotFound));
        tracker.AcknowledgeFailureMode(PromptFailureMode.PromptFileNotFound);

        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.PromptFileNotFound));

        var snapshot = tracker.GetSnapshot();
        var unacknowledged = snapshot.RewriteFailures[PromptFailureMode.PromptFileNotFound]
                           - snapshot.AcknowledgedFailures[PromptFailureMode.PromptFileNotFound];
        unacknowledged.ShouldBe(1);
    }

    [Fact]
    public void AcknowledgeFailureMode_PersistedAndRestoredOnRestart()
    {
        var tracker = Build();
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.IdentityNotRecognized));
        tracker.AcknowledgeFailureMode(PromptFailureMode.IdentityNotRecognized);

        // Simulate restart — new tracker instance loads from same directory
        var tracker2 = Build();
        var snapshot = tracker2.GetSnapshot();

        snapshot.AcknowledgedFailures[PromptFailureMode.IdentityNotRecognized].ShouldBe(1);
    }

    [Fact]
    public void AcknowledgeFailureMode_OtherModesUnaffected()
    {
        var tracker = Build();
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.PromptFileNotFound));
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.NoBulkBlock));

        tracker.AcknowledgeFailureMode(PromptFailureMode.PromptFileNotFound);

        var snapshot = tracker.GetSnapshot();
        snapshot.AcknowledgedFailures[PromptFailureMode.PromptFileNotFound].ShouldBe(1);
        snapshot.AcknowledgedFailures[PromptFailureMode.NoBulkBlock].ShouldBe(0);
    }
}
