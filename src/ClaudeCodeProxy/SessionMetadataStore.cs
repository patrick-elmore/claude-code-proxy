using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

// Friendly names are deliberately stored outside the daily usage files. A rename can then update
// labels for every day in which the opaque Claude Code session ID appears, without changing costs.
public sealed class SessionMetadataStore
{
    private const string FileName = "session-metadata.json";
    private readonly string _outputPath;
    private readonly string _filePath;
    private readonly ILogger<SessionMetadataStore> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PersistedSession> _sessions = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public SessionMetadataStore(IOptions<StatsOptions> opts, ILogger<SessionMetadataStore> logger)
    {
        _outputPath = opts.Value.OutputPath;
        _filePath = Path.Combine(_outputPath, FileName);
        _logger = logger;
        LoadFromDisk();
    }

    public SessionMetadataSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var sessions = _sessions.ToDictionary(
                pair => pair.Key,
                pair => new SessionMetadataSessionSnapshot(
                    pair.Value.DisplayName,
                    pair.Value.NameSource,
                    pair.Value.LastCwd,
                    pair.Value.LastObservedAt,
                    pair.Value.Agents.ToDictionary(
                        agent => agent.Key,
                        agent => new AgentMetadataSnapshot(agent.Value.AgentType, agent.Value.LastObservedAt),
                        StringComparer.Ordinal)),
                StringComparer.Ordinal);

            return new SessionMetadataSnapshot(sessions);
        }
    }

    // Metadata is best effort. Persistence failures leave the in-memory update available for the
    // current process and never propagate to the endpoint or to Claude Code.
    public void Upsert(SessionMetadataUpdate update)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(update.SessionId, out var session))
                _sessions[update.SessionId] = session = new PersistedSession();

            var observedAt = update.ObservedAt ?? DateTimeOffset.UtcNow;
            if (session.LastObservedAt is null || observedAt > session.LastObservedAt)
                session.LastObservedAt = observedAt;

            if (update.Cwd is not null)
                session.LastCwd = update.Cwd;

            if (update.Event == SessionMetadataEvent.SessionStart && update.SessionTitle is not null)
                ApplyDisplayName(session, update.SessionTitle, "session_start", priority: 1);
            else if (update.Event == SessionMetadataEvent.StatusLine && update.SessionName is not null)
                ApplyDisplayName(session, update.SessionName, "status_line", priority: 2);

            if (update.Event == SessionMetadataEvent.SubagentStart
                && update.AgentId is not null
                && update.AgentType is not null)
            {
                if (!session.Agents.TryGetValue(update.AgentId, out var agent))
                    session.Agents[update.AgentId] = agent = new PersistedAgent();
                agent.AgentType = update.AgentType;
                if (agent.LastObservedAt is null || observedAt > agent.LastObservedAt)
                    agent.LastObservedAt = observedAt;
            }

            PersistLocked();
        }
    }

    private static void ApplyDisplayName(PersistedSession session, string name, string source, int priority)
    {
        var existingPriority = session.NameSource switch
        {
            "status_line" => 2,
            "session_start" => 1,
            _ => 0,
        };

        if (priority < existingPriority) return;
        session.DisplayName = name;
        session.NameSource = source;
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var persisted = JsonSerializer.Deserialize<PersistedMetadata>(File.ReadAllText(_filePath), JsonOptions);
            if (persisted?.Sessions is null) return;

            foreach (var (id, session) in persisted.Sessions)
            {
                if (string.IsNullOrWhiteSpace(id) || session is null) continue;
                session.Agents ??= new Dictionary<string, PersistedAgent>(StringComparer.Ordinal);
                _sessions[id] = session;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session metadata; starting with an empty registry");
        }
    }

    private void PersistLocked()
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_outputPath);
            temporaryPath = Path.Combine(_outputPath, $".{FileName}.{Guid.NewGuid():N}.tmp");
            var persisted = new PersistedMetadata
            {
                Sessions = new Dictionary<string, PersistedSession>(_sessions, StringComparer.Ordinal),
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persisted, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist session metadata to disk");
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch { /* Metadata diagnostics must never become request failures. */ }
            }
        }
    }

    private sealed class PersistedMetadata
    {
        public Dictionary<string, PersistedSession>? Sessions { get; set; }
    }

    private sealed class PersistedSession
    {
        public string? DisplayName { get; set; }
        public string? NameSource { get; set; }
        public string? LastCwd { get; set; }
        public DateTimeOffset? LastObservedAt { get; set; }
        public Dictionary<string, PersistedAgent> Agents { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class PersistedAgent
    {
        public string? AgentType { get; set; }
        public DateTimeOffset? LastObservedAt { get; set; }
    }
}

public sealed record SessionMetadataSnapshot(
    IReadOnlyDictionary<string, SessionMetadataSessionSnapshot> Sessions);

public sealed record SessionMetadataSessionSnapshot(
    string? DisplayName,
    string? NameSource,
    string? LastCwd,
    DateTimeOffset? LastObservedAt,
    IReadOnlyDictionary<string, AgentMetadataSnapshot> Agents);

public sealed record AgentMetadataSnapshot(string? AgentType, DateTimeOffset? LastObservedAt);

public enum SessionMetadataEvent
{
    SessionStart,
    SubagentStart,
    StatusLine,
}

public sealed record SessionMetadataUpdate(
    SessionMetadataEvent Event,
    string SessionId,
    string? SessionTitle,
    string? SessionName,
    string? Cwd,
    string? AgentId,
    string? AgentType,
    DateTimeOffset? ObservedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SessionMetadataRequest
{
    public string? Event { get; init; }
    public string? SessionId { get; init; }
    public string? SessionTitle { get; init; }
    public string? SessionName { get; init; }
    public string? Cwd { get; init; }
    public string? AgentId { get; init; }
    public string? AgentType { get; init; }
    public string? Source { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }

    public bool TryCreateUpdate(out SessionMetadataUpdate? update, out string error)
    {
        update = null;
        if (!IsOptionalTextValid(Event, 40)
            || !IsOptionalIdentifierValid(SessionId)
            || !IsOptionalTextValid(SessionTitle, 512)
            || !IsOptionalTextValid(SessionName, 512)
            || !IsOptionalTextValid(Cwd, 2048)
            || !IsOptionalIdentifierValid(AgentId)
            || !IsOptionalTextValid(AgentType, 256)
            || !IsOptionalTextValid(Source, 256)
            || !IsOptionalTextValid(Model, 256))
        {
            error = "One or more metadata fields are invalid.";
            return false;
        }

        var eventName = Normalize(Event, 40);
        var sessionId = NormalizeIdentifier(SessionId);
        var sessionTitle = Normalize(SessionTitle, 512);
        var sessionName = Normalize(SessionName, 512);
        var cwd = Normalize(Cwd, 2048);
        var agentId = NormalizeIdentifier(AgentId);
        var agentType = Normalize(AgentType, 256);

        if (eventName is null || sessionId is null)
        {
            error = "event and session_id are required.";
            return false;
        }

        SessionMetadataEvent kind;
        switch (eventName)
        {
            case "session-start":
                kind = SessionMetadataEvent.SessionStart;
                if (agentId is not null || agentType is not null || sessionName is not null)
                {
                    error = "session-start only accepts session metadata.";
                    return false;
                }
                break;
            case "subagent-start":
                kind = SessionMetadataEvent.SubagentStart;
                if (agentId is null || agentType is null || sessionTitle is not null || sessionName is not null)
                {
                    error = "subagent-start requires agent_id and agent_type only.";
                    return false;
                }
                break;
            case "status-line":
                kind = SessionMetadataEvent.StatusLine;
                if (sessionName is null || agentId is not null || agentType is not null || sessionTitle is not null)
                {
                    error = "status-line requires session_name only.";
                    return false;
                }
                break;
            default:
                error = "event must be session-start, subagent-start, or status-line.";
                return false;
        }

        update = new SessionMetadataUpdate(kind, sessionId, sessionTitle, sessionName, cwd, agentId, agentType, ObservedAt);
        error = string.Empty;
        return true;
    }

    private static string? NormalizeIdentifier(string? value)
    {
        var normalized = Normalize(value, 256);
        return normalized is not null && normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength && !normalized.Any(char.IsControl) ? normalized : null;
    }

    private static bool IsOptionalTextValid(string? value, int maxLength) =>
        value is null || Normalize(value, maxLength) is not null;

    private static bool IsOptionalIdentifierValid(string? value) =>
        value is null || NormalizeIdentifier(value) is not null;
}
