# Claude Code Proxy

A local HTTP proxy that intercepts Claude Code's outbound requests to Google Vertex AI and replaces the static behavioral portion of the system prompt with a custom one. Everything else passes through unchanged.

## What it does

Claude Code ships with a fixed system prompt that defines its general behavior (how it does engineering tasks, tone, output style, tool-use rules, etc.). That prompt is generic — it has to work for every kind of user.

This proxy lets you swap that out for a system prompt tuned to your own preferences and working style, while leaving every other piece of Claude Code's plumbing intact:

- The Claude Code identity prefix is preserved (so Anthropic's prefix-cache routing still works).
- The session-specific dynamic content (`# Session-specific guidance`, `# auto memory`, `# Environment`, `# Context management`, gitStatus) is preserved verbatim — Claude Code's per-project memory, environment info, and git context all keep working.
- The `messages` array, `tools` definitions, request metadata, and response stream are all forwarded byte-for-byte.
- Google authentication passes through transparently — the proxy holds no credentials.

The mutation only touches the static behavioral text in `system[2]` between the identity prefix and `# Session-specific guidance`.

## Requirements

- **Vertex AI only.** This proxy is for Claude Code installations using `CLAUDE_CODE_USE_VERTEX=1`. Anthropic-direct-API and Bedrock are not supported.
- **Windows host.** The proxy runs as a Windows service. WSL is fine for development but the binary itself is Windows-targeted.
- **.NET 10 SDK** for building.
- **Administrator privileges** to install as a Windows service.

## Setup

### 1. Build and install the service

From an **elevated** PowerShell (Run as Administrator):

```powershell
cd C:\misc\claude-code-proxy
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
```

The script:

1. Verifies `.NET 10 SDK` and `ASP.NET Core 10 runtime` are present (fails with a download URL if not).
2. Publishes binaries to `%LOCALAPPDATA%\ClaudeCodeProxy\`.
3. On first install, rewrites the installed `appsettings.json` to point `Proxy:PromptFilePath` at the in-repo prompt file (`prompts\default.md`) and `Dump:OutputPath` at `%LOCALAPPDATA%\ClaudeCodeProxy\dumps\`. On re-install, your existing `appsettings.json` is preserved.
4. Registers an auto-start Windows service named `ClaudeCodeProxy`.

The script starts the service automatically once it's registered. The service binds to `127.0.0.1:9996` (loopback only).

The custom prompt file stays in this source repo (`prompts\default.md`) so you can edit it under version control. The installed `appsettings.json` references its absolute path. If you move or rename the source repo, re-run `install.ps1` to update the path.

### 2. Point Claude Code at the proxy

In the shell where you run `claude`, set:

```bash
# WSL / bash
export ANTHROPIC_VERTEX_BASE_URL=http://localhost:9996
```

```powershell
# Windows PowerShell
$env:ANTHROPIC_VERTEX_BASE_URL = "http://127.0.0.1:9996"
```

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
| `PromptFilePath` | `C:/misc/claude-code-proxy/prompts/default.md` | Absolute path to the file containing your custom system prompt. If empty or missing, the proxy passes requests through unmodified (no mutation, but still parses/reserializes — for true byte-for-byte passthrough use `Enabled: false`). |
| `VertexBaseUrl` | `https://aiplatform.googleapis.com/v1` | Upstream Vertex endpoint, including the `/v1` segment. Use the global host (no region prefix) when working with `locations/global`; use `https://{region}-aiplatform.googleapis.com/v1` for regional endpoints. |
| `Variables` | `{}` | Optional dictionary of `{{key}}` → value substitutions applied to the prompt file at request time. Reserved for future use. |

### `Dump` section

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | When true, captures the inbound request body before and after mutation. Auto-disables after `MaxRequests` reached. |
| `MaxRequests` | `5` | Number of original requests to capture before auto-disabling. |
| `OutputPath` | `C:/misc/claude-code-proxy/dumps` | Directory for dump files. **Use a Windows-style path** — the service runs as a Windows process and misinterprets WSL-style `/mnt/c/...` paths as `C:\mnt\c\...`. |

### `Serilog` section

Standard Serilog configuration. By default, logs go to `./logs/proxy_YYYYMMDD.log` (relative to the binary location) at Information level.

After changing `appsettings.json`, restart the service:

```powershell
Restart-Service ClaudeCodeProxy
```

## Customizing the system prompt

The default prompt at `prompts/default.md` is tuned for software engineering, technical writing, and blog writing. To customize:

1. Edit `prompts/default.md` directly, or
2. Create a new file (anywhere) and point `Proxy:PromptFilePath` at it in `appsettings.json`.

The proxy reads the file fresh on every request, so prompt edits take effect immediately — no service restart needed.

### What to put in the prompt

The replacement is the **static behavioral portion only**. You don't need to include anything related to:

- Claude Code identity ("You are Claude Code...") — preserved automatically.
- Available tools, skills, or subagents — Claude Code injects this in the dynamic portion after your prompt.
- Auto-memory system, current working directory, OS, model, current date, or git status — all in the dynamic portion.
- The JSON-tool-call instruction Claude Code appends — preserved automatically.

Focus your prompt on principles, behavior rules, communication style, and approach to work. Keep the same shape as Claude Code's original (markdown headers, prose paragraphs, ` - ` bullet style — no XML wrapping).

### What gets replaced vs. preserved

For each inbound `POST /projects/.../models/{model}:rawPredict` (or `:streamRawPredict`):

1. The proxy parses the JSON body and finds the `system` array.
2. It identifies blocks by content:
   - Block starting with `x-anthropic-billing-header` → preserved (attribution).
   - Block matching one of the three known Claude Code identity-prefix strings → preserved.
   - The longest remaining block → the bulk block.
3. Within the bulk block, it searches for the first occurrence of `\n# Session-specific guidance\n` (with `\n# auto memory\n` and `\n# Environment\n` as fallbacks).
4. Everything **before** that boundary is replaced with the custom prompt; everything from the boundary onward is preserved verbatim.
5. The mutated body is forwarded to the configured Vertex endpoint with the original auth header attached.

If any condition is unmet (no `system` field, body isn't JSON, no boundary marker, etc.), the proxy forwards the request unmodified — fail-safe to transparent passthrough.

## Debug mode

When you want to inspect what's actually on the wire, enable dump mode:

```json
"Dump": {
  "Enabled": true,
  "MaxRequests": 5,
  "OutputPath": "C:/misc/claude-code-proxy/dumps"
}
```

Restart the service. The next 5 requests will write three files each to the dump directory:

- `{timestamp}-original.json` — body as received from Claude Code (pretty-printed JSON if it parses)
- `{timestamp}-mutated.json` — body after the prompt rewrite (only when mutation actually happened)
- `{timestamp}-path.txt` — the request path including query string

After 5 requests, dumping auto-disables to avoid filling disk. Re-enabling requires another `appsettings.json` edit + restart.

## Service management

Three scripts at the repo root, all requiring an **elevated** PowerShell:

```powershell
# First-time install — verifies prerequisites, builds, registers, starts the service.
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1

# Update — rebuilds and redeploys binaries (preserving your appsettings.json),
# stopping and restarting the service.
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
echo $ANTHROPIC_VERTEX_BASE_URL    # should print http://localhost:9996
```

If empty in the shell where you launched `claude`, set it and start a fresh `claude` session.

If set correctly but still not intercepting, confirm the proxy is listening:

```powershell
netstat -ano | findstr 9996
```

### TLS / connection errors in the log

If you see `SocketException` or `Unable to read data from the transport connection` errors, the most likely cause is an incorrect `VertexBaseUrl`. For `locations/global`, the host is `aiplatform.googleapis.com` (not `global-aiplatform.googleapis.com`). For regional endpoints, use `{region}-aiplatform.googleapis.com`.

### 401 / 403 from Vertex

Auth issue. Make sure `CLAUDE_CODE_SKIP_VERTEX_AUTH` is **not** set — Claude Code needs to generate real Google Bearer tokens for the proxy to forward.

### My custom prompt isn't being applied

Check that `Proxy:PromptFilePath` in `appsettings.json` points to a file that exists. The proxy logs a warning when the file is missing and passes through unmodified. Inspect `proxy_YYYYMMDD.log` for the warning.

Run the verification prompt from the Setup section to confirm.

### "Off-machine actions" guidance feels too restrictive

The bullet in the default prompt scopes off-machine actions to "on your own initiative" — meaning explicit user requests for SSH, cloud calls, etc. don't trigger confirmation. If the model is over-confirming, edit `prompts/default.md` to soften that bullet's wording.

## Project layout

```
claude-code-proxy/
├── README.md                  # this file
├── install.ps1                # first-time service install (admin-required)
├── update.ps1                 # rebuild + redeploy, preserve config (admin-required)
├── uninstall.ps1              # stop + remove service (admin-required)
├── ClaudeCodeProxy.slnx       # solution
├── prompts/
│   └── default.md             # the active replacement prompt (edit here)
└── src/ClaudeCodeProxy/
    ├── ClaudeCodeProxy.csproj
    ├── Program.cs             # service host + single catch-all route
    ├── PromptRewriter.cs      # block-level mutation
    ├── HeaderCopying.cs       # hop-by-hop header filter
    ├── Options.cs             # ProxyOptions + DumpOptions
    ├── RequestDumper.cs       # debug dump
    ├── appsettings.json       # source-repo defaults (install.ps1 rewrites paths)
    └── appsettings.Development.json

# Runtime artifacts (created by install.ps1):
%LOCALAPPDATA%\ClaudeCodeProxy\
├── ClaudeCodeProxy.exe        # service binary
├── appsettings.json           # active config (preserved across updates)
├── logs\                      # Serilog file output
└── dumps\                     # debug dumps when Dump:Enabled = true
```
