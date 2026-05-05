#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the Claude Code Proxy Windows service. Optionally
    deletes the install directory (logs, dumps, installed appsettings).

.DESCRIPTION
    The source repository (this folder) is left untouched.
    The user is prompted before deleting the install directory so that
    custom appsettings, dumps, and logs aren't lost by accident.
#>

$ErrorActionPreference = "Stop"

$ScriptDir   = $PSScriptRoot
$InstallDir  = Join-Path $env:LOCALAPPDATA "ClaudeCodeProxy"
$LegacyDir   = Join-Path $ScriptDir "publish"
$ServiceName = "ClaudeCodeProxy"

# --- Stop and remove the service ---
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Write-Host "Stopping service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    sc.exe delete $ServiceName | Out-Null

    # Wait for the SCM to actually release the service.
    $timeout = 15
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and $timeout -gt 0) {
        Start-Sleep -Seconds 1
        $timeout--
    }
    Write-Host "Service '$ServiceName' removed."
} else {
    Write-Host "Service '$ServiceName' not found. Skipping service removal."
}

# --- Optionally remove the new install directory ---
if (Test-Path $InstallDir) {
    Write-Host ""
    Write-Host "Install directory: $InstallDir"
    Write-Host "Deleting it removes:"
    Write-Host "  - The service binary"
    Write-Host "  - Your appsettings.json customizations"
    Write-Host "  - Captured dumps (if any)"
    Write-Host "  - Service logs"
    $confirm = Read-Host "Delete install directory? (y/N)"
    if ($confirm -eq 'y') {
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "Install directory removed."
    } else {
        Write-Host "Install directory preserved at $InstallDir"
    }
} else {
    Write-Host "Install directory not present at $InstallDir. Nothing to remove."
}

# --- Clean up legacy install location (older install.ps1 published here) ---
if (Test-Path $LegacyDir) {
    Write-Host ""
    Write-Host "Legacy install directory found: $LegacyDir"
    Write-Host "(Older install.ps1 versions published binaries here. Safe to delete now.)"
    $confirmLegacy = Read-Host "Delete legacy install directory? (y/N)"
    if ($confirmLegacy -eq 'y') {
        Remove-Item $LegacyDir -Recurse -Force
        Write-Host "Legacy install directory removed."
    } else {
        Write-Host "Legacy install directory preserved at $LegacyDir"
    }
}

Write-Host "Uninstall complete."
