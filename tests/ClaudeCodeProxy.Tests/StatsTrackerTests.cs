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

    [Fact]
    public void SessionAndAgentAttribution_PersistedAndRestored()
    {
        var tracker = Build();
        tracker.Record(Request("session-1", null, null, "vertex", input: 100, output: 10));
        tracker.Record(Request("session-1", "agent-1", null, "vertex", input: 200, output: 20));
        tracker.Record(Request("session-1", "agent-2", "agent-1", "anthropic", input: 300, output: 30));
        tracker.RecordFailed(Request("session-1", "agent-2", "agent-1", "anthropic"));
        tracker.Record(Request(null, null, null, "vertex", input: 400, output: 40));

        var snapshot = tracker.GetSnapshot();
        snapshot.Sessions.ShouldNotBeNull();
        var sessions = snapshot.Sessions!;
        var session = sessions.Single(s => s.SessionId == "session-1");
        session.Requests.ShouldBe(3);
        session.Failed.ShouldBe(1);
        session.Totals.InputUncached.ShouldBe(600);
        session.Agents.Single(a => a.AgentId is null).Totals.Output.ShouldBe(10);
        var child = session.Agents.Single(a => a.AgentId == "agent-2");
        child.ParentAgentId.ShouldBe("agent-1");
        child.ByProvider["anthropic"].Requests.ShouldBe(1);
        child.ByProvider["anthropic"].Failed.ShouldBe(1);
        sessions.Single(s => s.SessionId is null).Totals.InputUncached.ShouldBe(400);

        var restored = Build().GetSnapshot();
        var restoredSessions = restored.Sessions!;
        var restoredSession = restoredSessions.Single(s => s.SessionId == "session-1");
        restoredSession.Agents.Single(a => a.AgentId == "agent-2").ParentAgentId.ShouldBe("agent-1");
        restoredSessions.Single(s => s.SessionId is null).Totals.Output.ShouldBe(40);
    }

    [Fact]
    public void LegacyStatsWithoutSessionData_AppearAsUnattributed()
    {
        var date = Build().GetSnapshot().Date.Replace("-", "");
        var path = Path.Combine(_statsDir, $"stats-{date}.json");
        File.WriteAllText(path, $$"""
            {
              "date": "{{date[..4]}}-{{date[4..6]}}-{{date[6..]}}",
              "started_at": "2026-01-01T00:00:00+00:00",
              "request_count": 1,
              "failed_count": 0,
              "totals": { "input_uncached": 123, "cache_read": 0, "cache_creation5m": 0, "cache_creation1h": 0, "output": 45 },
              "total_duration_ms": 1,
              "rewrite_failures": {},
              "acknowledged_failures": {},
              "unknown_paths": [],
              "by_model": {
                "vertex|test-model": {
                  "requests": 1, "failed": 0, "input_uncached": 123, "cache_read": 0,
                  "cache_creation5m": 0, "cache_creation1h": 0, "output": 45,
                  "total_duration_ms": 1, "model": "test-model", "provider": "vertex"
                }
              },
              "hourly": [],
              "by_endpoint": {}
            }
            """);

        var snapshot = Build().GetSnapshot();
        snapshot.Sessions.ShouldNotBeNull();
        var unattributed = snapshot.Sessions!.Single();
        unattributed.SessionId.ShouldBeNull();
        unattributed.Totals.InputUncached.ShouldBe(123);
        unattributed.Agents.Single().ByProvider["vertex"].Output.ShouldBe(45);
    }

    private static RequestStats Request(string? sessionId, string? agentId, string? parentAgentId,
        string provider, int input = 0, int output = 0) => new("test", DateTimeOffset.UtcNow)
    {
        SessionId = sessionId,
        AgentId = agentId,
        ParentAgentId = parentAgentId,
        Provider = provider,
        Model = "test-model",
        InputUncached = input,
        OutputTokens = output,
        DurationMs = 1,
    };
}
