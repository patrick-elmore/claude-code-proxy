#!/bin/bash
INPUT=$(cat)
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command')

if echo "$COMMAND" | grep -q 'COMMIT_SCANNED=1'; then
    exit 0
fi

jq -n '{
    hookSpecificOutput: {
        hookEventName: "PreToolUse",
        permissionDecision: "deny",
        permissionDecisionReason: "Sensitive content scan required before committing. Run git diff --cached and review the staged changes for: API keys, tokens, or secrets; passwords or credentials; real names of people (teammates, managers, customers); employer or company names; internal product codenames or team names; absolute paths containing personal usernames, home directories, or non-relative paths to files within this repo; internal acronyms specific to one organization. If the diff is clean, prefix your commit command with COMMIT_SCANNED=1 and retry."
    }
}'
