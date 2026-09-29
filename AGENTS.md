# AGENTS.md

This file provides guidance to coding agents when working with code in this repository.

## Build

```powershell
# Debug
dotnet.exe build src/ClaudeCodeProxy/ClaudeCodeProxy.csproj

# Release (what install.ps1 and update.ps1 use)
dotnet.exe build src/ClaudeCodeProxy/ClaudeCodeProxy.csproj -c Release
```

The solution file is `ClaudeCodeProxy.slnx`.

## Tests

- `tests/ClaudeCodeProxy.Tests` contains unit tests.
- `tests/ClaudeCodeProxy.IntegrationTests` contains Razor endpoint integration
  tests using `WebApplicationFactory` and AngleSharp.

```powershell
# Debug
dotnet.exe test ClaudeCodeProxy.slnx

# Release
dotnet.exe test ClaudeCodeProxy.slnx -c Release
```

## Deployment

The service is installed, updated, and removed via three PowerShell scripts at the repo root — all require an elevated shell. `install.ps1` publishes to `%LOCALAPPDATA%\ClaudeCodeProxy\`, registers a Windows service named `ClaudeCodeProxy`, and rewrites `appsettings.json` paths on first install only (existing config is preserved on re-install). `update.ps1` rebuilds and redeploys without touching config.

Day-to-day: `Restart-Service ClaudeCodeProxy` after editing `appsettings.json` in the install directory. The prompt file is authored at `prompts/default.md` in the repo and deployed to `%LOCALAPPDATA%\ClaudeCodeProxy\prompts\default.md` by `dotnet publish` (wired in the csproj) — the service reads the deployed copy through a 1-minute cache in `PromptRewriter`. Editing the repo prompt requires `update.ps1` to take effect; editing the deployed copy directly applies within a minute.

## Architecture

### Request pipeline

Every inbound request hits the catch-all route (`{**path}`) in `Program.cs`. For requests with a body:

1. Body is read into memory.
2. If `Proxy:Enabled`, the body is parsed as JSON and passed to `PromptRewriter.RewriteAsync`.
3. The (possibly mutated) bytes are forwarded to `Proxy:VertexBaseUrl` + original path, with original auth headers passed through.
4. `UsageLogger.TapAndForwardAsync` streams the response to the client while parsing it inline for token usage. It handles both SSE (`streamRawPredict`) and plain JSON (`rawPredict`) response shapes.
5. Extracted usage is handed to `StatsTracker.Record` (or `RecordFailed`), which persists to disk immediately.

`IOptionsMonitor<T>` is used in `PromptRewriter`, `RequestDumper`, and the catch-all handler so that `Proxy:Enabled`, `VertexBaseUrl`, and dump settings respond to `appsettings.json` changes without restart. `StatsTracker` uses `IOptions<T>` because its output path is fixed at startup.

### PromptRewriter

Block identification is by content, not array position — order of blocks in `system[]` is not assumed. The rewriter:

1. Skips any block starting with `x-anthropic-billing-header` (attribution).
2. Skips any block matching one of the three known Claude Code identity prefix strings (from `src/constants/system.ts` in the Claude Code CLI source).
3. Treats the longest remaining block as the "bulk block" containing the behavioral system prompt.
4. Within the bulk block, finds the first occurrence of `\n# Session-specific guidance\n` (with `\n# auto memory\n` and `\n# Environment\n` as fallbacks) as the boundary between static and dynamic content.
5. Replaces everything before the boundary with the contents of `Proxy:PromptFilePath`.

Any unmet condition (missing `system` field, no bulk block, no boundary marker, missing prompt file) results in a silent passthrough — the proxy never fails a request due to mutation failure.

### Pricing

`PricingCatalog` loads `pricing.json` at startup (USD per million tokens, standard tier only) and re-checks the file's last-write time at most every 5 minutes, hot-swapping the rates when it changes and keeping the old rates if the new file fails to parse. A model may carry a `promotions` array of date-bounded rate overrides — selection is by the date being priced (the stats snapshot's date for historical renders, today for live), first matching window wins. Expired promotions must stay in the file: historical stats pages re-price past days on render. The file is also refreshed by `PricingUpdater` (hourly: downloads the `pricing.md` docs page, regenerates the file deterministically in memory, and rewrites it only when the SHA-256 differs, then invalidates the catalog; any fetch, parse, or sanity-check failure leaves the file untouched) and manually via the `update-pricing` skill (`.claude/skills/update-pricing/`), which pulls Anthropic's published pricing page.

### Stats

`StatsTracker` accumulates all state in memory under a single `Lock` and writes the full snapshot to `stats-YYYYMMDD.json` after every request. It rolls over at midnight. Historical dates are loaded from disk on demand in `GetSnapshotForDate`.

`StatsHtmlRenderer` has two entry points: `Render()` for single-day views and `RenderRange()` for week/month aggregates. Both produce self-contained HTML with inline CSS. Style reference: `.context/stats-page-style-guide.md`.

The favicon (`wwwroot/favicon.svg`) is read once at startup and served via `MapGet` — `UseStaticFiles` is not used because SVG MIME type resolution is inconsistent across ASP.NET Core versions and deployment contexts.

### Path handling

The Vertex SDK path may include a `/v1` prefix that the configured `VertexBaseUrl` also ends with. The catch-all handler strips the trailing `/v1` from `baseUrl` when the request path already starts with `/v1/` to avoid doubling.

## File Conventions

- **Line endings**: CRLF (`\r\n`) for all files except `.sh` scripts.
- **WSL**: use `dotnet.exe`, `git.exe` — not the bare commands.
- **Dump paths**: always Windows-style (`C:\...`) — the service process misinterprets WSL-style `/mnt/c/...` paths.

## C# Conventions

### Collection return types

Choose the return type based on how callers use the collection, not what the internal implementation uses.

| Situation | Use |
|---|---|
| Public method; callers shouldn't mutate | `IReadOnlyCollection<T>` / `IReadOnlyDictionary<TKey,TValue>` |
| Callers need a snapshot independent of internal state | `ImmutableList<T>` / `ImmutableArray<T>` |
| Thread-shared state replaced wholesale on update | `ImmutableList<T>` / `ImmutableDictionary<TKey,TValue>` |
| Built once at startup, read many times | `FrozenSet<T>` / `FrozenDictionary<TKey,TValue>` (.NET 8+) |

`IReadOnly*` is a view, not a copy — internal mutations to the backing `List<T>` are visible to callers. Use `Immutable*` when callers need a guaranteed snapshot. Never create `FrozenSet`/`FrozenDictionary` in hot paths (per-request, per-loop); startup or cache initialization only.

### Async

- Use `await` over `Task.FromResult`. Let async flow naturally through the call stack.
- Never use `.Result` or `.GetAwaiter().GetResult()` — they block the thread and deadlock in ASP.NET contexts.
- Never `yield return` from inside a `using` block. The resource is disposed before the caller enumerates.

### Data structures

- Use `HashSet<T>` for uniqueness, not `List<T>` with `.Distinct()`.
- Use indexer assignment (`dict[key] = value`) when building a dictionary from data whose uniqueness isn't guaranteed. `dict.Add` throws on duplicate keys.

### Error handling

- Don't swallow exceptions in consumers or background workers. Log and re-throw, or don't catch at all.
- Operations that may be retried or replayed must be idempotent.

### Performance

- No I/O calls inside loops. Fetch all needed data before iteration, then process in-memory.

---

## Approach

**Verify before acting.** Treat user statements, error messages, and external suggestions as hypotheses, not facts. Read the code before accepting them. Raise concerns when a user statement conflicts with what you observe.

**Know why before executing.** Understand what you expect from a tool call or command before making it. No speculative tool use. If you can't articulate why a specific action will help, don't take it.

**Follow existing patterns; don't invent.** Match 2-3 codebase examples before writing new code. Don't add abstractions, wrappers, or utilities that don't already exist. If no pattern fits, ask rather than improvising.

**Assume existing code is intentional.** Code that looks redundant or wrong is often load-bearing. Investigate before simplifying; flag concerns rather than silently changing.

**Own your failures.** When builds fail after your changes, assume your changes caused it until proven otherwise.

**Never re-run a successful operation unless inputs changed.**

---

## Interaction Protocol

- When the prompt is a **question**, answer it and suggest fixes if applicable. Don't make changes without explicit approval.
- **Directive prompts** ("fix this", "add that") imply approval to act — but for directives that reference specific code or suggest a specific implementation, verify assumptions before acting.
- When the user corrects formatting or structure on one item, apply the correction across all similar items and re-print the full corrected output.
- **Never commit or push without explicit approval.** Staging files for review is fine; completing the commit is not.
- **When told to stop, stop permanently** for the rest of the session.

---

## Doing Tasks

Match scope to the request. Don't add features, refactor, abstract, or clean up beyond what was asked. No half-finished work.

For exploratory questions, respond in 2-3 sentences with a recommendation and the main tradeoff. Wait for go-ahead before implementing.

**Default to no comments.** Add one only when the *why* is non-obvious. Never explain what the code does. No multi-line comment blocks; one short line maximum.

Don't add error handling for impossible scenarios. Trust internal code; validate only at system boundaries.

Don't leave backwards-compat hacks (renamed `_vars`, `// removed` comments, dead flags). Delete what's unused.

---

## Discover Before Asking

The codebase is your first source of truth.

**Don't ask users about**: naming conventions, file structure, error handling patterns, logging format, configuration keys, dependency injection patterns.

**Search code instead**: find 2-3 similar implementations, extract the pattern, apply it. 3+ consistent examples = use without asking. 1-2 examples or conflicting patterns = flag and ask.

**Do ask users about**: business decisions, user-facing text, missing requirements, choices when no pattern exists.

When looking for patterns, classify by what the code *does* (a database query, a message handler) not what domain it belongs to.

---

## Precision Standards

Every piece of information must be exact: property names not descriptions, types not category labels, exact message text, hex codes not color names, sizes with units, performance with metrics.

When you get an imprecise answer, ask follow-up questions until you have an exact specification. Eliminate all "OR" conditions before writing anything.

---

## Continuous Improvement

When running iterative processes, look for recurring patterns in the output and improve the system that produces them. If the same class of issue appears 3+ times, change the system — don't note it and move on.

Apply this to your own behavior. If the user corrects the same type of mistake twice, identify and internalize the rule that would have prevented both instances.

---

## Terminate on Non-Recoverable Errors

When running a multi-step process, evaluate after each failure whether the error will cascade. If so, stop immediately, report why it's non-recoverable, and ask the user to resolve it before re-running.

---

## Commits

Before staging and committing, review the full diff (`git diff`) for sensitive content: credentials, tokens, API keys, personal data, internal URLs with auth, or anything that shouldn't be public. Also confirm the commit message itself contains none of this. Only proceed once the diff is clean.

## Commits — Sensitive Content Scan

Before committing, always run these steps in order:

1. Stage files with a standalone `git add` — never chain `git add && git commit` in one command. The commit hook fires on any Bash command containing `git commit` and will deny the entire compound command, including the add.
2. Run `git diff --cached` and review the staged diff for:
   - API keys, tokens, secrets, or passwords
   - Real names of people (teammates, managers, customers)
   - Employer or company names, internal product codenames or team names
   - Absolute paths containing personal usernames or home directories
   - Internal acronyms or terminology specific to one organization
3. If the diff is clean, prefix the commit command with `COMMIT_SCANNED=1`:

```
COMMIT_SCANNED=1 git -C /mnt/c/misc/claude-code-proxy commit -m "message"
```

A project hook enforces this — commits without the prefix will be blocked and the reason injected back into context.

---

## AI Attribution

Never attribute work to AI in commit messages, PR descriptions, code comments, or documentation. Write all content as if produced by any team member.

---

## Writing Style

Lead with the key fact, outcome, or decision — no preamble. Short to medium sentences; one idea per sentence. Fragments are acceptable.

Use technical terms without definition. State what was done, what the result was, and what is outstanding — in that order.

Express uncertainty with "I think", "I suspect", "I'm not sure." Don't hedge confident statements.

Use bullet lists only when there are three or more distinct items.

**Do not**: use em dashes; use exclamation points except in rare humorous moments; open with praise ("Great question", "Absolutely"); use bold or caps for emphasis in conversational content; close with "let me know if you have any questions."

---

## Pull Request Comments

Post review findings as inline comments on the relevant file and line, not top-level PR comments.

- Frame issues as questions, not directives.
- Categorize by severity: blocking issue, suggestion, or nit.
- Avoid "you" language. "This function could..." not "You should..."
- Name the exact problem and its consequence.
- Leave compliments when warranted.
