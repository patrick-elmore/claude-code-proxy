#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Rebuilds and redeploys the Claude Code Proxy, preserving appsettings.json,
    and restarts the service.

.DESCRIPTION
    Use this after pulling code changes. Stops the service if running,
    backs up appsettings.json, runs dotnet publish into the install directory
    (which would otherwise overwrite appsettings.json), restores appsettings,
    then starts the service.

    Requires that install.ps1 has already been run.
#>

$ErrorActionPreference = "Stop"

$ScriptDir   = $PSScriptRoot
$ProjectDir  = Join-Path $ScriptDir "src\ClaudeCodeProxy"
$InstallDir  = Join-Path $env:LOCALAPPDATA "ClaudeCodeProxy"
$AppSettings = Join-Path $InstallDir "appsettings.json"
$ServiceName = "ClaudeCodeProxy"

if (-not (Test-Path $InstallDir)) {
    # Detect the legacy install case: service exists but was installed from
    # an older install.ps1 that put binaries in <repo>\publish\ instead of
    # %LOCALAPPDATA%\ClaudeCodeProxy.
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        $existingBinPath = (Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).PathName
        Write-Error @"
Install directory not found at $InstallDir, but service '$ServiceName' is registered.
This usually means you installed using an older version of install.ps1 that put
the service under a different path:
  $existingBinPath

To migrate to the new install layout:
  1. powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
  2. powershell.exe -ExecutionPolicy Bypass -File .\install.ps1

Both scripts are idempotent and the new install will end with the service running.
"@
        exit 1
    }

    Write-Error "Install directory not found at $InstallDir. Run install.ps1 first."
    exit 1
}
if (-not (Test-Path $AppSettings)) {
    Write-Error "Installed appsettings.json not found at $AppSettings. Run install.ps1 first."
    exit 1
}

# --- Backup user appsettings ---
Write-Host "Backing up appsettings.json..."
$appSettingsBackup = Get-Content $AppSettings -Raw

# --- Stop service if running ---
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Write-Host "Stopping service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force
    }
} else {
    Write-Warning "Service '$ServiceName' not registered. Update will redeploy binaries but the service won't be created — run install.ps1 if you need the service."
}

# --- Build / publish (overwrites appsettings.json among other files) ---
Write-Host "Building..."
dotnet publish $ProjectDir --configuration Release --runtime win-x64 --self-contained false --output $InstallDir
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }
Write-Host "Build complete."

# --- Restore user appsettings ---
Write-Host "Restoring appsettings.json..."
Set-Content $AppSettings -Value $appSettingsBackup -Encoding UTF8

# --- Start service ---
if ($service) {
    Write-Host "Starting service '$ServiceName'..."
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        $svc = Get-Service -Name $ServiceName
        Write-Host "Service '$ServiceName' is $($svc.Status)."
    } catch {
        Write-Warning "Service failed to start after update: $_"
        Write-Warning "Try: Start-Service $ServiceName"
    }
}

Write-Host "Update complete."
