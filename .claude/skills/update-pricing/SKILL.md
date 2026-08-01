---
name: update-pricing
description: Refresh src/ClaudeCodeProxy/pricing.json from Anthropic's published pricing page, including any time-boxed promotional rates. Use when asked to update, refresh, or sync model pricing.
---

# Update Pricing

Sync `src/ClaudeCodeProxy/pricing.json` with Anthropic's current published pricing.

## Steps

1. **Fetch the pricing page.** WebFetch `https://platform.claude.com/docs/en/about-claude/pricing` (fall back to `https://platform.claude.com/docs/en/pricing.md` if it fails). Ask for: per-model prices per million tokens — base input, output, 5-minute cache write, 1-hour cache write, cache hit/refresh — plus any long-context (>200K) pricing tiers and any promotional or limited-time pricing language with its date range.

2. **Read the current `src/ClaudeCodeProxy/pricing.json`** to get the existing model keys and schema.

3. **Map page names to model IDs.** The page uses friendly names (e.g. "Claude Opus 4.8"); pricing.json keys are API model IDs (e.g. `claude-opus-4-8`). Match against existing keys first. For a model new to the file, use the API model ID from the page or the docs models overview — never construct an ID by guessing.

4. **Update rates in place.**
   - `standard` gets the page's regular (non-promotional) rates. Cache multipliers, when the page gives only input/output: write-5m = 1.25× input, write-1h = 2× input, hit = 0.1× input.
   - `long_context` (only for models the page lists a >200K tier for) gets that tier's rates. Preserve existing `long_context` blocks when the page doesn't contradict them.
   - Add models the page lists that are missing from the file.
   - Never delete a model absent from the page — old models still appear in historical stats. Mention it in the summary instead.

## Promotional rates

If the page says a rate is promotional, limited-time, or introductory for a stated period:

- Keep the regular rate in `standard` (if the page only shows the promo rate, the regular rate is stated in the promo verbiage or the previous file value — ask the user if genuinely ambiguous).
- Encode the promo as an entry in that model's `promotions` array:

```json
"promotions": [
  {
    "description": "verbatim or near-verbatim promo wording from the page",
    "starts": "2026-07-01",
    "ends": "2026-09-30",
    "rates": { "input": 2.50, "output": 12.50, "cache_write_5m": 3.125, "cache_write_1h": 5.00, "cache_hit": 0.25 }
  }
]
```

- Dates are `yyyy-MM-dd`, inclusive on both ends. If the page gives only a vague window ("through Q3"), resolve to concrete dates and note the interpretation in the summary.
- **Never remove expired promotions.** Historical stats pages re-price past days on render, so a June promo must stay in the file after it ends or June's costs go wrong.
- `PricingCatalog` applies the first promo whose window contains the date, else `standard`.

## Finish

1. Validate the file parses: `python3 -c "import json; json.load(open('src/ClaudeCodeProxy/pricing.json'))"`.
2. Summarize what changed: per model, old → new rates, promos added, and any models on the page that were skipped or absent.
3. Remind that the change reaches the running service on the next `update.ps1` deploy (elevated shell), or by copying `pricing.json` to `%LOCALAPPDATA%\ClaudeCodeProxy\` and running `Restart-Service ClaudeCodeProxy`. Do not deploy without being asked.
