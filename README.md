# Claude Code Proxy

A local HTTP proxy that intercepts Claude Code's outbound requests — to Google Vertex AI, and optionally to the direct Anthropic API — and replaces the static behavioral portion of the system prompt with a custom one. Everything else passes through unchanged.

## What it does

Claude Code ships with a fixed system prompt that defines its general behavior (how it does engineering tasks, tone, output style, tool-use rules, etc.). That prompt is generic — it has to work for every kind of user.

This proxy lets you swap that out for a system prompt tuned to your own preferences and working style, while leaving every other piece of Claude Code's plumbing intact:

- The Claude Code identity block is preserved (so Anthropic's prefix-cache routing still works).
- The session-specific dynamic content (`# Session-specific guidance`, `# auto memory`, `# Environment`, `# Context management`, gitStatus) is preserved verbatim — Claude Code's per-project memory, environment info, and git context all keep working.
- The `messages` array, `tools` definitions, request metadata, and response stream are all forwarded byte-for-byte.
- Upstream authentication passes through transparently — the proxy doesn't use your credentials to make requests of its own. It does keep the most recently seen bearer token in memory for diagnostic inspection; see [Diagnostics](#diagnostics).

Blocks in the `system[]` array are identified by content, not position: the known Claude Code identity string(s) and the Anthropic billing-attribution block are recognized and skipped, and the longest remaining block is treated as the behavioral prompt to replace.

The proxy also runs in **dual mode**: it can front a Vertex AI-backed Claude Code session, a direct-Anthropic-API-backed session, or both at once (routing is per-request, based on the request path), and it tracks usage stats and cost separately per provider.

## Requirements

- **Windows host.** The proxy runs as a Windows service. WSL is fine for development but the binary itself is Windows-targeted.
- **Vertex AI and/or the direct Anthropic API.** Point Claude Code at the proxy using whichever mode(s) you use — `CLAUDE_CODE_USE_VERTEX=1` for Vertex, or the standard Anthropic API base URL override for direct-API mode. Amazon Bedrock is not supported.
- **.NET 10 SDK** for building.
- **Administrator privileges** to install as a Windows service.

## Setup

### 1. Build and install the service

From an **elevated** PowerShell (Run as Administrator), `cd` into your clone of this repo, then:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
```

The script:

1. Verifies `.NET 10 SDK` and `ASP.NET Core 10 runtime` are present (fails with a download URL if not).
2. Publishes binaries to `%LOCALAPPDATA%\ClaudeCodeProxy\`. The prompt file (`prompts\default.md`) is deployed alongside the binaries as part of publish.
3. On first install, rewrites the installed `appsettings.json` to point `Proxy:PromptFilePath` at the deployed prompt copy (`%LOCALAPPDATA%\ClaudeCodeProxy\prompts\default.md`), and `Dump:OutputPath` / `Stats:OutputPath` at their own subdirectories under the install directory. On re-install, your existing `appsettings.json` is preserved.
4. Merges two best-effort metadata hooks into your user-level Claude Code settings and adds the status-line publisher directly to a file-based PowerShell status-line script. This lets the dashboard display friendly session names and subagent types; existing hooks and visible status output are preserved.
5. Registers an auto-start Windows service named `ClaudeCodeProxy`.

The script starts the service automatically once it's registered. The service binds to `127.0.0.1:9996` (loopback only).

The prompt file is authored in this source repo (`prompts\default.md`) but the running service reads its own deployed copy, refreshed on a 1-minute cache — not the repo file directly. After editing `prompts\default.md`, run `update.ps1` to rebuild and redeploy it (see [Customizing the system prompt](#customizing-the-system-prompt)).

### 2. Point Claude Code at the proxy

For **Vertex AI** mode, in the shell where you run `claude`:

```bash
# WSL / bash
export ANTHROPIC_VERTEX_BASE_URL=http://localhost:9996
```

```powershell
# Windows PowerShell
$env:ANTHROPIC_VERTEX_BASE_URL = "http://127.0.0.1:9996"
```

For **direct Anthropic API** mode, set the standard Anthropic base URL override (e.g. `ANTHROPIC_BASE_URL=http://localhost:9996`) instead, and configure `Proxy:AnthropicBaseUrl` in `appsettings.json` (see [Configuration](#configuration)) so the proxy knows where to forward `/v1/messages` requests. You can run both modes against the same proxy instance simultaneously — it routes each request based on its path shape.

Persist these in your shell profile (`.bashrc`, PowerShell `$PROFILE`) once you've confirmed everything works.

Do **not** set `CLAUDE_CODE_SKIP_VERTEX_AUTH` for normal use — Claude Code needs to generate real Google Bearer tokens, which the proxy passes through to Vertex.

### 3. Verify the proxy is intercepting

Start a new `claude` session and ask:

> Quote the first bullet from the section called "# Approach" in your system prompt. If there is no such section, say "no Approach section".

If the proxy is working with the default prompt, you should get back a quote starting with "Verify before acting." If you get "no Approach section," the proxy isn't being hit — most likely an env-var issue.

## Configuration

All configuration lives in `appsettings.json` next to the published binary (`%LOCALAPPDATA%\ClaudeCodeProxy\appsettings.json`).

### `Proxy` section

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Master switch for system-prompt rewriting. When `false`, the proxy forwards every request byte-for-byte to the upstream — no JSON parse, no rewriter call, no mutation. The service stays listening; only the mutation step is skipped. Useful for A/B testing the prompt against the default Claude Code behavior without uninstalling the service. |
| `PromptFilePath` | `prompts/default.md` | Path to the file containing your custom system prompt (relative to the binary location, or absolute). `install.ps1` points this at the deployed prompt copy under the install directory on first install. The loaded content is cached for 1 minute — see [Customizing the system prompt](#customizing-the-system-prompt). If empty or the file is missing, the proxy passes requests through unmodified (no mutation, but still parses/reserializes — for true byte-for-byte passthrough use `Enabled: false`). |
| `VertexBaseUrl` | `https://aiplatform.googleapis.com/v1` | Upstream Vertex endpoint, including the `/v1` segment. Use the global host (no region prefix) when working with `locations/global`; use `https://{region}-aiplatform.googleapis.com/v1` for regional endpoints. |
| `AnthropicBaseUrl` | `""` (empty) | Upstream direct-Anthropic-API endpoint, e.g. `https://api.anthropic.com` — **no** trailing `/v1`. Requests to `/v1/messages*` are routed here instead of Vertex, but only when this is set; leaving it empty means those paths fall through to `VertexBaseUrl` like everything else. |
| `Variables` | `{}` | Optional dictionary of `{{key}}` → value substitutions applied to the prompt file at request time. Reserved for future use. |

### `Dump` section

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | When true, captures the inbound request body before and after mutation for the first `MaxRequests` requests, then auto-disables. |
| `MaxRequests` | `5` | Number of original requests to capture (under `Enabled`) before auto-disabling. |
| `FailureDumpEnabled` | `false` | When true, captures the raw request body for every request that hits a tracked rewrite-failure mode (see the failure banners on the [stats dashboard](#stats-dashboard)) — independent of `Enabled`/`MaxRequests`, so it keeps running until you turn it off. Capped at 50 MB/day to avoid filling disk on a persistent failure. |
| `OutputPath` | `<set-to-a-writable-directory>` | Directory for dump files (shared by both `Enabled` and `FailureDumpEnabled`). The default in source is a placeholder so dumps don't accidentally land in the working tree — `install.ps1` rewrites this to `%LOCALAPPDATA%\ClaudeCodeProxy\dumps` on first install. When running outside the installed service, set this explicitly before enabling either dump mode. **Use a Windows-style path** — the service runs as a Windows process and misinterprets WSL-style `/mnt/c/...` paths as `C:\mnt\c\...`. |

### `HeaderDump` section

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Diagnostic only. When true, appends every incoming request's headers (including `Authorization`) to `C:\tmp\headers.txt` for every request. Used for debugging auth handshakes when standing up a new environment; leave off otherwise. See [Diagnostics](#diagnostics). |

### `Stats` section

| Key | Default | Purpose |
|---|---|---|
| `OutputPath` | `./stats` | Directory where daily stats files are written. `install.ps1` creates this under `%LOCALAPPDATA%\ClaudeCodeProxy\stats\`. |
| `DayBoundaryTimeZone` | `America/New_York` | IANA time zone id the stats day boundary is evaluated in. Falls back to the server's local time zone (with a logged warning) if the configured id is invalid. |
| `DayBoundaryTime` | `00:00:00` | Wall-clock time in `DayBoundaryTimeZone` where a new stats day begins. `00:00:00` (the default) means each stats day runs local midnight to midnight. A boundary of `20:00:00` would mean the period from 8 PM on day D to 8 PM on day D+1 is labeled D+1 (the day the period ends on). |
| `MonthlyTargetSpendUsd` | `5500` | Monthly spending budget in USD. The stats dashboard divides it by the number of days in the current month to show the target daily spend. |

### `Serilog` section

Standard Serilog configuration. By default, logs go to `./logs/proxy_YYYYMMDD.log` (relative to the binary location) at Information level.

After changing `appsettings.json`, restart the service:

```powershell
Restart-Service ClaudeCodeProxy
```

## Customizing the system prompt

The default prompt at `prompts/default.md` is a working example covering engineering approach, tool-use rules, and communication style — the same shape as Claude Code's original. To customize:

1. Edit `prompts/default.md` directly, or
2. Create a new file (anywhere) and point `Proxy:PromptFilePath` at it in `appsettings.json`.

The prompt file is deployed to the install directory as part of `dotnet publish`, not read live from the repo — after editing `prompts/default.md`, run `update.ps1` (elevated) to rebuild and redeploy it. `PromptRewriter` caches the loaded content for 1 minute, so once the deployed file changes, edits apply within a minute with no service restart needed. If you pointed `PromptFilePath` at a file outside the repo instead, that file is read live subject to the same 1-minute cache.

### What to put in the prompt

The replacement is the **static behavioral portion only**. You don't need to include anything related to:

- Claude Code identity ("You are Claude Code...") — preserved automatically.
- Available tools, skills, or subagents — Claude Code injects this in the dynamic portion after your prompt.
- Auto-memory system, current working directory, OS, model, current date, or git status — all in the dynamic portion.
- The JSON-tool-call instruction Claude Code appends — preserved automatically.

Focus your prompt on principles, behavior rules, communication style, and approach to work. Keep the same shape as Claude Code's original (markdown headers, prose paragraphs, ` - ` bullet style — no XML wrapping).

### What gets replaced vs. preserved

For each inbound request that carries a `system[]` array — Vertex's `POST /projects/.../models/{model}:rawPredict` (or `:streamRawPredict`), or the direct API's `POST /v1/messages` — the proxy:

1. Parses the JSON body and finds the `system` array. Requests with `max_tokens: 1` (Claude Code's model-availability probe) are skipped untouched.
2. Identifies blocks by content:
   - Block starting with `x-anthropic-billing-header` → preserved (attribution).
   - Block matching one of the known Claude Code identity-prefix strings → preserved.
   - The longest remaining block → the bulk block.
3. If no known identity block is present, the request is passed through unchanged. This excludes Claude Code auxiliary traffic such as Agent View status summaries and safely handles future unknown request shapes.
4. If no bulk block is found but an identity block was recognized (an agent-SDK sub-agent request with no behavioral prompt of its own), the request is left alone — nothing to replace.
5. Otherwise, within the bulk block, it searches for the first occurrence of `\n# Session-specific guidance\n` (with `\n# auto memory\n` and `\n# Environment\n` as fallbacks).
6. Everything **before** that boundary is replaced with the custom prompt; everything from the boundary onward is preserved verbatim. If no boundary is found at all (e.g. a genuine sub-agent or custom-prompted request), the custom prompt is inserted as a new block instead of replacing text in place.
7. The mutated body is forwarded to the configured upstream (Vertex or Anthropic, depending on the path and whether `Proxy:AnthropicBaseUrl` is set) with the original auth header attached.

If any condition is unmet (no `system` field, body isn't JSON, no boundary marker, prompt file missing, etc.), the proxy forwards the request unmodified — fail-safe to transparent passthrough. The [stats dashboard](#stats-dashboard) surfaces these as dismissable failure banners so you notice when rewriting silently stops happening.

## Diagnostics

### Dump mode

When you want to inspect what's actually on the wire, enable dump mode:

```json
"Dump": {
  "Enabled": true,
  "MaxRequests": 5,
  "OutputPath": "C:/path/to/dumps"
}
```

Restart the service. The next 5 requests will write files to the dump directory:

- `{timestamp}-original.json` — body as received from Claude Code (pretty-printed JSON if it parses)
- `{timestamp}-mutated.json` — body after the prompt rewrite (only when mutation actually happened)
- `{timestamp}-path.txt` — the request path including query string

After 5 requests, dumping auto-disables to avoid filling disk. Re-enabling requires another `appsettings.json` edit + restart.

To instead capture only requests that hit a rewrite failure (regardless of how many), set `Dump:FailureDumpEnabled` to `true`. This writes `{timestamp}-failure-{Mode}.json` (plus a matching `-path.txt`) for every failing request and keeps running — capped at 50 MB/day rather than a request count — so it's suited to catching an intermittent failure over a longer window.

### Header and auth-token inspection

Two diagnostic-only tools help debug enterprise auth handshakes when standing up a new environment:

- Set `HeaderDump:Enabled` to `true` to append every incoming request's headers (including `Authorization`) to `C:\tmp\headers.txt`. Leave this off outside active debugging — it's unfiltered and includes credentials.
- `GET /internal/auth-token` (loopback-only, same as everything else the service exposes) returns the most recently seen `Authorization` header value, so you can confirm the token Claude Code is sending without turning on full header dumping.

## Stats dashboard

The proxy exposes a Razor Pages usage dashboard at `http://localhost:9996/stats`.

| Endpoint | Description |
|---|---|
| `/stats` | Today's (current stats day's) live view |
| `/stats/{date}` | Historical view for a specific date (`yyyy-MM-dd`) |
| `/stats/week` | Last 7 days aggregate |
| `/stats/month` | Last 30 days aggregate |
| `/stats.json` | Today's data as raw JSON |
| `/stats/{date}.json` | Historical data as raw JSON |

Append `?provider=vertex` or `?provider=anthropic` to any HTML view to filter to just that upstream (buttons for this are also on the page itself); omit it to see both combined.

Each view shows request count, prompt-injected and passed-through counts, cache hit rate, average latency, estimated cost, token breakdown by type, per-model breakdown (annotated with provider when both are in use), a Claude Code session breakdown, an endpoint-routing breakdown (which upstream URLs actually received traffic), and hourly activity. Prompt-handling counts cover parsed model requests evaluated by the rewriter and are global across providers. The session section nests subagents under their recorded parents and attributes their token usage and estimated cost separately; requests without `x-claude-code-session-id` appear as **Unattributed**. The existing provider filter applies to these session and agent totals too. When available, a friendly main-session title and subagent type appear above their full stored IDs. When a rewrite failure mode has occurred (missing prompt file, unrecognized identity block, unknown request path, etc.), a dismissable banner surfaces it at the top of the page; dismissing posts to `/stats/dismiss/{mode}` and only re-appears if the failure count increases again.

Stats are written to disk after each request as `stats-YYYYMMDD.json` in `Stats:OutputPath` and persist across service restarts. Session, agent, and parent-agent IDs are stored in full in those local files. Friendly metadata is separately stored as `session-metadata.json` in the same directory, so a later rename improves labels on historical views without changing cost data. Older stats files without header attribution are represented as Unattributed. The "day" a request counts toward is controlled by `Stats:DayBoundaryTimeZone`/`Stats:DayBoundaryTime` (see [Configuration](#configuration)). Cost estimates use each model's standard rate (with any active promotional rate applied for the date being priced) from `src/ClaudeCodeProxy/pricing.json`, refreshed via the `update-pricing` Claude Code skill in `.claude/skills/update-pricing/`.

### Friendly Claude Code session labels

`install.ps1` and `update.ps1` deploy scripts under `%LOCALAPPDATA%\ClaudeCodeProxy\claude-hooks\` and merge their entries into `%USERPROFILE%\.claude\settings.json`. They do not modify project settings. Before changing a status-line script or the settings file, the installer writes a complete copy to `%LOCALAPPDATA%\ClaudeCodeProxy\config-backups\claude-code-metadata\`. The integration sends only the opaque session/agent IDs, working directory, session title/name, and subagent type to the proxy's loopback-only `POST /internal/session-metadata` endpoint. It never sends prompts or transcript content.

- `SessionStart` seeds a known session title and project context; `SubagentStart` associates a friendly agent type such as `Explore` or `Plan` with the agent ID.
- Because Claude Code has no documented rename hook, the installer adds a small publisher directly to a file-based PowerShell status-line script. It observes the current `session_name`, including `/rename` and Claude-generated titles, while the script continues to render its normal output. A non-file-based custom status-line command is left unchanged and reported as unable to capture rename/title metadata.
- The metadata request has a hard one-second timeout and every request, parse, cache, and persistence failure is ignored. The status-line publisher uses a per-session `%TEMP%` timestamp cache, so it sends at most one request per session per 30 seconds. The status line refreshes every 30 seconds to observe an idle rename.
- The integration is best effort. If the proxy is stopped, metadata is unavailable, or Claude Code runs in safe mode, Claude Code continues normally and the dashboard falls back to raw IDs. If your organization enables `allowManagedHooksOnly`, the installer leaves the user hook configuration untouched and reports that a managed deployment is required.

The endpoint accepts only small, validated metadata payloads and is bound to `127.0.0.1`; it must not be exposed through a reverse proxy or LAN binding.

## Service management

Three scripts at the repo root, all requiring an **elevated** PowerShell:

```powershell
# First-time install — verifies prerequisites, builds, registers, starts the service.
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1

# Update — rebuilds and redeploys binaries and the prompt file (preserving
# your appsettings.json), stopping and restarting the service.
powershell.exe -ExecutionPolicy Bypass -File .\update.ps1

# Uninstall — stops and removes the service. Prompts before deleting the
# install directory (which contains your appsettings.json, dumps, and logs).
powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Day-to-day service control:

```powershell
Get-Service ClaudeCodeProxy           # status
Stop-Service ClaudeCodeProxy          # stop
Start-Service ClaudeCodeProxy         # start
Restart-Service ClaudeCodeProxy       # restart (e.g., after editing appsettings.json)
```

Logs from the running service land in `%LOCALAPPDATA%\ClaudeCodeProxy\logs\proxy_YYYYMMDD.log`.

## Troubleshooting

### The proxy isn't intercepting

Most common cause: env-var didn't propagate to the `claude` process.

```bash
echo $ANTHROPIC_VERTEX_BASE_URL    # should print http://localhost:9996 (Vertex mode)
echo $ANTHROPIC_BASE_URL           # should print http://localhost:9996 (direct-API mode)
```

If empty in the shell where you launched `claude`, set it and start a fresh `claude` session.

If set correctly but still not intercepting, confirm the proxy is listening:

```powershell
netstat -ano | findstr 9996
```

### TLS / connection errors in the log

If you see `SocketException` or `Unable to read data from the transport connection` errors, the most likely cause is an incorrect `VertexBaseUrl` or `AnthropicBaseUrl`. For Vertex `locations/global`, the host is `aiplatform.googleapis.com` (not `global-aiplatform.googleapis.com`). For regional endpoints, use `{region}-aiplatform.googleapis.com`. For direct Anthropic, the host is `api.anthropic.com` with no trailing `/v1`.

### 401 / 403 from Vertex

Auth issue. Make sure `CLAUDE_CODE_SKIP_VERTEX_AUTH` is **not** set — Claude Code needs to generate real Google Bearer tokens for the proxy to forward. See [Header and auth-token inspection](#header-and-auth-token-inspection) to confirm what the proxy is actually receiving.

### `/v1/messages` requests aren't reaching Anthropic

Confirm `Proxy:AnthropicBaseUrl` is actually set in the *installed* `appsettings.json` — an empty value silently falls through to `VertexBaseUrl` instead of erroring, by design, so a missing config value looks like the request just went to the wrong place rather than failing outright.

### My custom prompt isn't being applied

Check that `Proxy:PromptFilePath` in `appsettings.json` points to a file that exists. The proxy logs a warning when the file is missing and passes through unmodified. Inspect `proxy_YYYYMMDD.log` for the warning.

If the path is correct but a recent edit doesn't seem to have taken effect, remember the loaded prompt is cached for up to 1 minute, and that editing the repo's `prompts/default.md` requires running `update.ps1` to redeploy it — editing the repo file alone does not change what the running service reads.

Run the verification prompt from the Setup section to confirm.

### "Off-machine actions" guidance feels too restrictive

The bullet in the default prompt scopes off-machine actions to "on your own initiative" — meaning explicit user requests for SSH, cloud calls, etc. don't trigger confirmation. If the model is over-confirming, edit `prompts/default.md` to soften that bullet's wording.

## Project layout

```
claude-code-proxy/
├── README.md                  # this file
├── LICENSE
├── install.ps1                # first-time service install (admin-required)
├── update.ps1                 # rebuild + redeploy, preserve config (admin-required)
├── uninstall.ps1              # stop + remove service (admin-required)
├── ClaudeCodeProxy.slnx       # solution
├── .claude/skills/update-pricing/  # keeps pricing.json in sync with Anthropic's published rates
├── prompts/
│   └── default.md             # the active replacement prompt (edit here, then run update.ps1)
└── src/ClaudeCodeProxy/
    ├── ClaudeCodeProxy.csproj # publishes prompts/default.md alongside the binary
    ├── Program.cs             # service host, routes, catch-all proxy handler, Vertex/Anthropic routing
    ├── PromptRewriter.cs      # block-level system-prompt mutation, 1-minute prompt cache
    ├── PromptFailureMode.cs   # RewriteOutcome / failure-mode enum shared by tracking and dumps
    ├── HeaderCopying.cs       # hop-by-hop header filter
    ├── HeaderDumper.cs        # diagnostic: dumps request headers to C:\tmp\headers.txt when enabled
    ├── AuthTokenStore.cs      # diagnostic: last-seen Authorization header, exposed via /internal/auth-token
    ├── Options.cs             # ProxyOptions, DumpOptions, HeaderDumpOptions, StatsOptions
    ├── RequestDumper.cs       # debug dump (original/mutated bodies, and failure-mode dumps)
    ├── UsageLogger.cs         # SSE tap and usage extraction, tagged with provider/upstream endpoint
    ├── StatsTracker.cs        # per-day stats accumulation and persistence, by model/provider/endpoint
    ├── StatsDay.cs            # computes the current stats day under the configurable day boundary
    ├── StatsNavigation.cs     # prev/next date links for the stats dashboard
    ├── Presentation/Stats/    # typed stats presentation models and calculations
    ├── Pages/Stats/           # Razor Pages exposed under /stats*
    ├── Pages/Shared/          # stats Razor layout and typed partials
    ├── PricingCatalog.cs      # rate lookup, cost calculation, date-bounded promotional rates
    ├── pricing.json           # rate catalog (standard + promotional rates, per model)
    ├── appsettings.json       # source-repo defaults (install.ps1 rewrites paths)
    ├── appsettings.Development.json
    └── wwwroot/
        ├── favicon.svg        # favicon served by the static-asset manifest
        └── css/stats.css      # stats stylesheet served by the static-asset manifest

# Runtime artifacts (created by install.ps1):
%LOCALAPPDATA%\ClaudeCodeProxy\
├── ClaudeCodeProxy.exe        # service binary
├── prompts\default.md         # deployed prompt copy (the service reads this, not the repo file)
├── appsettings.json           # active config (preserved across updates)
├── logs\                      # Serilog output
├── dumps\                     # debug dumps when Dump:Enabled or Dump:FailureDumpEnabled = true
└── stats\                     # daily stats JSON files
```
