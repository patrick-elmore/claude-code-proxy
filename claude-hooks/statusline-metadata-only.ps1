#!/usr/bin/env pwsh
# Silent status line for users who have no existing status-line command.
$rawInput = [Console]::In.ReadToEnd()
try {
    $d = $rawInput | ConvertFrom-Json -ErrorAction Stop
    $cwd = ($d.workspace?.current_dir ?? $d.cwd ?? $d.workspace?.project_dir) ?? '.'
    if (-not [string]::IsNullOrWhiteSpace($d.session_name) -and $d.session_id) {
        . (Join-Path $PSScriptRoot 'statusline-metadata-publisher.ps1')
        Publish-ClaudeCodeSessionNameMetadata -SessionId ([string]$d.session_id) -SessionName ([string]$d.session_name) -Cwd $cwd
    }
}
catch {
    # No visible status line and no failure propagation.
}
