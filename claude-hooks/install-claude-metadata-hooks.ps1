#!/usr/bin/env pwsh
[CmdletBinding()]
param()

# Installs only this proxy's entries into user-scoped Claude Code settings. Session-name telemetry
# is injected directly into file-based status lines because a wrapper cannot reliably relay stdin
# JSON to an arbitrary existing status-line command on Windows.
$ErrorActionPreference = 'Stop'

function Ensure-ObjectProperty([psobject]$Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue ([pscustomobject]@{})
    }
    return $Object.$Name
}

function Set-ObjectProperty([psobject]$Object, [string]$Name, [object]$Value) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $property.Value = $Value
    }
}

function Add-ProxyHook([psobject]$Hooks, [string]$EventName, [string]$EventArgument, [string]$PublisherPath) {
    $existing = @($Hooks.$EventName)
    $alreadyPresent = $false
    foreach ($entry in $existing) {
        foreach ($handler in @($entry.hooks)) {
            $hasPublisher = @($handler.args) -contains $PublisherPath
            $hasEventArgument = @($handler.args) -contains $EventArgument
            if ($handler.type -eq 'command' -and $handler.command -eq 'pwsh.exe' -and $hasPublisher -and $hasEventArgument) {
                $alreadyPresent = $true
                break
            }
        }
        if ($alreadyPresent) { break }
    }
    if ($alreadyPresent) { return }

    $handler = [pscustomobject]@{
        type = 'command'
        command = 'pwsh.exe'
        args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PublisherPath, '-Event', $EventArgument)
        async = $true
        timeout = 1
    }
    $entry = [pscustomobject]@{ hooks = @($handler) }
    if ($null -eq $Hooks.PSObject.Properties[$EventName]) {
        $Hooks | Add-Member -NotePropertyName $EventName -NotePropertyValue @($entry)
    }
    else {
        $Hooks.$EventName = @($existing + $entry)
    }
}

function Backup-File([string]$SourcePath, [string]$BackupDirectory) {
    if (-not (Test-Path -LiteralPath $SourcePath)) { return }
    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
    $destination = Join-Path $BackupDirectory "$([IO.Path]::GetFileNameWithoutExtension($SourcePath)).$timestamp$([IO.Path]::GetExtension($SourcePath))"
    Copy-Item -LiteralPath $SourcePath -Destination $destination -Force
    Write-Host "Backed up: $destination"
}

function Get-StatusLineScriptPath([string]$Command) {
    if ($Command -notmatch '(?i)(?:^|\s)-File\s+(?:"(?<path>[^"]+)"|(?<path>\S+))') { return $null }
    $path = [Environment]::ExpandEnvironmentVariables($Matches['path'])
    if (Test-Path -LiteralPath $path -PathType Leaf) { return (Resolve-Path -LiteralPath $path).Path }
    return $null
}

function Get-OriginalCommandFromLegacyWrapper([string]$Command, [string]$HooksDirectory) {
    if ($Command -notlike '*statusline-metadata-wrapper.ps1*') { return $Command }
    $configPath = Join-Path $HooksDirectory 'statusline-wrapper.json'
    if (-not (Test-Path -LiteralPath $configPath)) { return $null }
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -ErrorAction Stop
    return [string]$config.original_command
}

function New-StatusLineInjection([string]$PublisherPath) {
    $escapedPath = $PublisherPath.Replace("'", "''")
    $block = @'
# >>> ClaudeCodeProxy session metadata >>>
try {
    . '__PUBLISHER_PATH__'
    $ccpSessionName = $d.session_name
    if (-not [string]::IsNullOrWhiteSpace($ccpSessionName) -and $d.session_id) {
        Publish-ClaudeCodeSessionNameMetadata -SessionId ([string]$d.session_id) -SessionName ([string]$ccpSessionName) -Cwd $cwd
    }
}
catch {
    # Session metadata is strictly best effort and must never affect status-line rendering.
}
# <<< ClaudeCodeProxy session metadata <<<
'@
    return $block.Replace('__PUBLISHER_PATH__', $escapedPath)
}

function Install-IntoStatusLineScript([string]$ScriptPath, [string]$PublisherPath, [string]$BackupDirectory) {
    $content = Get-Content -LiteralPath $ScriptPath -Raw
    $injection = New-StatusLineInjection $PublisherPath
    $markerPattern = '(?ms)^# >>> ClaudeCodeProxy session metadata >>>\r?\n.*?^# <<< ClaudeCodeProxy session metadata <<<\r?\n?'
    $legacyPattern = '(?ms)^# Session-label telemetry is embedded directly in the live status line so it receives Claude\r?\n# Code''s JSON itself\. It is fully best effort: no request or cache failure may affect rendering\.\r?\n.*?(?=^# Change to working directory)'

    $existingBlock = [regex]::Match($content, $markerPattern)
    if ($existingBlock.Success) {
        $updated = $content.Remove($existingBlock.Index, $existingBlock.Length).Insert($existingBlock.Index, "$injection`r`n")
    }
    else {
        $existingBlock = [regex]::Match($content, $legacyPattern)
        if ($existingBlock.Success) {
            $updated = $content.Remove($existingBlock.Index, $existingBlock.Length).Insert($existingBlock.Index, "$injection`r`n")
        }
        else {
            $anchor = [regex]::Match($content, '(?m)^\$sessionId\s*=.*\r?\n')
            if (-not $anchor.Success) {
                Write-Warning "Could not find a session_id assignment in $ScriptPath; left the status line unchanged."
                return $false
            }
            $updated = $content.Insert($anchor.Index + $anchor.Length, "`r`n$injection`r`n")
        }
    }

    if ($updated -eq $content) { return $true }
    Backup-File $ScriptPath $BackupDirectory
    Set-Content -LiteralPath $ScriptPath -Value $updated -Encoding utf8NoBOM -NoNewline
    Write-Host "Added Claude Code session metadata publishing to $ScriptPath"
    return $true
}

try {
    $hooksDirectory = $PSScriptRoot
    $installDirectory = Split-Path -Parent $hooksDirectory
    $publisherPath = Join-Path $hooksDirectory 'publish-metadata.ps1'
    $statusLinePublisherPath = Join-Path $hooksDirectory 'statusline-metadata-publisher.ps1'
    $metadataOnlyStatusLinePath = Join-Path $hooksDirectory 'statusline-metadata-only.ps1'
    $backupDirectory = Join-Path $installDirectory 'config-backups\claude-code-metadata'
    $settingsDirectory = Join-Path $env:USERPROFILE '.claude'
    $settingsPath = Join-Path $settingsDirectory 'settings.json'
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null

    $settings = if (Test-Path -LiteralPath $settingsPath) {
        Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    else {
        [pscustomobject]@{}
    }

    if ($settings.allowManagedHooksOnly -eq $true) {
        Write-Warning 'Claude Code allowManagedHooksOnly is enabled; user-installed session metadata hooks were not configured.'
        exit 0
    }

    $hooks = Ensure-ObjectProperty $settings 'hooks'
    Add-ProxyHook $hooks 'SessionStart' 'session-start' $publisherPath
    Add-ProxyHook $hooks 'SubagentStart' 'subagent-start' $publisherPath

    $currentStatusLine = $settings.statusLine
    if ($null -eq $currentStatusLine) {
        $settings | Add-Member -NotePropertyName 'statusLine' -NotePropertyValue ([pscustomobject]@{
            type = 'command'
            command = "pwsh.exe -NoProfile -ExecutionPolicy Bypass -File `"$metadataOnlyStatusLinePath`""
            refreshInterval = 30
        })
    }
    elseif ($currentStatusLine.type -ne 'command') {
        Write-Warning 'The existing Claude Code status line is not a command; it was left unchanged, so rename/title capture is unavailable.'
    }
    else {
        $originalCommand = Get-OriginalCommandFromLegacyWrapper $currentStatusLine.command $hooksDirectory
        if ([string]::IsNullOrWhiteSpace($originalCommand)) {
            Write-Warning 'The prior metadata wrapper has no recoverable original status-line command; it was left unchanged.'
        }
        else {
            $scriptPath = Get-StatusLineScriptPath $originalCommand
            if ($null -eq $scriptPath) {
                Write-Warning "The existing status-line command is not a file-based PowerShell script; it was left unchanged, so rename/title capture is unavailable."
            }
            elseif (Install-IntoStatusLineScript $scriptPath $statusLinePublisherPath $backupDirectory) {
                Set-ObjectProperty $currentStatusLine 'command' $originalCommand
                Set-ObjectProperty $currentStatusLine 'refreshInterval' 30
            }
        }
    }

    if (Test-Path -LiteralPath $settingsPath) { Backup-File $settingsPath $backupDirectory }
    $settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Write-Host "Claude Code session metadata hooks configured in $settingsPath"
}
catch {
    Write-Warning "Could not configure Claude Code session metadata hooks: $_"
    exit 0
}

exit 0
