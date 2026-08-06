using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class SessionMetadataStoreTests : IDisposable
{
    private readonly string _statsDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public SessionMetadataStoreTests() => Directory.CreateDirectory(_statsDir);

    public void Dispose() => Directory.Delete(_statsDir, recursive: true);

    [Fact]
    public void Upsert_PersistsNamesAndAgentTypes_AndStatusLineWinsOverSessionStart()
    {
        var store = Build(_statsDir);
        store.Upsert(new SessionMetadataUpdate(
            SessionMetadataEvent.SessionStart, "session-1", "Early title", null, "C:\\work\\auth", null, null,
            DateTimeOffset.Parse("2026-08-06T12:00:00Z")));
        store.Upsert(new SessionMetadataUpdate(
            SessionMetadataEvent.SubagentStart, "session-1", null, null, "C:\\work\\auth", "agent-1", "Explore",
            DateTimeOffset.Parse("2026-08-06T12:01:00Z")));
        store.Upsert(new SessionMetadataUpdate(
            SessionMetadataEvent.StatusLine, "session-1", null, "Auth refactor", "C:\\work\\auth", null, null,
            DateTimeOffset.Parse("2026-08-06T12:02:00Z")));
        store.Upsert(new SessionMetadataUpdate(
            SessionMetadataEvent.SessionStart, "session-1", "Stale title", null, "C:\\work\\auth", null, null,
            DateTimeOffset.Parse("2026-08-06T12:03:00Z")));

        var session = store.GetSnapshot().Sessions["session-1"];
        session.DisplayName.ShouldBe("Auth refactor");
        session.NameSource.ShouldBe("status_line");
        session.Agents["agent-1"].AgentType.ShouldBe("Explore");

        File.Exists(Path.Combine(_statsDir, "session-metadata.json")).ShouldBeTrue();
        var restored = Build(_statsDir).GetSnapshot().Sessions["session-1"];
        restored.DisplayName.ShouldBe("Auth refactor");
        restored.Agents["agent-1"].AgentType.ShouldBe("Explore");
    }

    [Theory]
    [InlineData("status-line", "session-1", "Friendly name", true)]
    [InlineData("status-line", "session id with spaces", "Friendly name", false)]
    [InlineData("status-line", "session-1", "", false)]
    [InlineData("unknown", "session-1", "Friendly name", false)]
    public void RequestValidation_OnlyAcceptsExpectedSessionMetadata(
        string eventName,
        string sessionId,
        string sessionName,
        bool expected)
    {
        var request = new SessionMetadataRequest
        {
            Event = eventName,
            SessionId = sessionId,
            SessionName = sessionName,
        };

        request.TryCreateUpdate(out var update, out _).ShouldBe(expected);
        if (expected)
        {
            update.ShouldNotBeNull();
            update!.Event.ShouldBe(SessionMetadataEvent.StatusLine);
        }
    }

    [Fact]
    public void Upsert_WhenMetadataFileCannotBeWritten_KeepsTheSessionInMemory()
    {
        var blockedOutputPath = Path.Combine(_statsDir, "not-a-directory");
        File.WriteAllText(blockedOutputPath, "block directory creation");
        var store = Build(blockedOutputPath);

        Should.NotThrow(() => store.Upsert(new SessionMetadataUpdate(
            SessionMetadataEvent.StatusLine, "session-1", null, "Friendly name", null, null, null, null)));

        store.GetSnapshot().Sessions["session-1"].DisplayName.ShouldBe("Friendly name");
    }

    private static SessionMetadataStore Build(string outputPath) =>
        new(new OptionsWrapper<StatsOptions>(new StatsOptions { OutputPath = outputPath }),
            NullLogger<SessionMetadataStore>.Instance);
}
