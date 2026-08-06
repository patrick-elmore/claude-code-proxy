#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('session-start', 'subagent-start')]
    [string]$Event
)

# This script is invoked by asynchronous Claude Code hooks. Telemetry must never affect Claude:
# every failure is swallowed, the request is capped at one second, and stdout stays empty.
$ErrorActionPreference = 'Stop'

function Get-NonEmptyText([object]$Value) {
    if ($null -eq $Value) { return $null }
    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text
}

function Publish-Metadata([hashtable]$Payload) {
    $client = $null
    $content = $null
    $response = $null
    try {
        $json = $Payload | ConvertTo-Json -Compress -Depth 4
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(1)
        $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
        $response = $client.PostAsync('http://127.0.0.1:9996/internal/session-metadata', $content).GetAwaiter().GetResult()
        # A non-success status is deliberately ignored. The next hook/status-line observation can repair it.
    }
    catch {
        # Best effort only: never block or add context to Claude Code.
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $content) { $content.Dispose() }
        if ($null -ne $client) { $client.Dispose() }
    }
}

try {
    $rawInput = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($rawInput)) { exit 0 }
    $input = $rawInput | ConvertFrom-Json -ErrorAction Stop
    $sessionId = Get-NonEmptyText $input.session_id
    if ($null -eq $sessionId) { exit 0 }

    $payload = [ordered]@{
        event = $Event
        session_id = $sessionId
        cwd = Get-NonEmptyText $input.cwd
        observed_at = [DateTimeOffset]::UtcNow.ToString('O')
        source = 'claude-code-hook'
    }

    if ($Event -eq 'session-start') {
        $payload.session_title = Get-NonEmptyText $input.session_title
        $payload.model = Get-NonEmptyText $input.model
    }
    else {
        $agentId = Get-NonEmptyText $input.agent_id
        $agentType = Get-NonEmptyText $input.agent_type
        if ($null -eq $agentId -or $null -eq $agentType) { exit 0 }
        $payload.agent_id = $agentId
        $payload.agent_type = $agentType
    }

    Publish-Metadata $payload
}
catch {
    # Parse, JSON, and local-environment failures are intentionally invisible to Claude Code.
}

exit 0
