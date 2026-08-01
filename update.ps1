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
$BackupDir  = Join-Path $InstallDir "config-backups"
if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
$timestamp  = Get-Date -Format "yyyyMMdd-HHmmss"
$backupFile = Join-Path $BackupDir "appsettings.$timestamp.json"
Copy-Item $AppSettings $backupFile -Force
Write-Host "Config backed up to $backupFile"

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

# Merge any new keys from the freshly-published defaults into the live config.
# Only adds keys that are absent; existing user values are never overwritten.
function Merge-MissingKeys {
    param([PSCustomObject]$Live, [PSCustomObject]$Template)
    foreach ($key in ($Template | Get-Member -MemberType NoteProperty).Name) {
        if (-not ($Live | Get-Member -MemberType NoteProperty -Name $key -ErrorAction SilentlyContinue)) {
            $Live | Add-Member -MemberType NoteProperty -Name $key -Value $Template.$key
            Write-Host "  + $key (new default from repo)"
        } elseif ($Template.$key -is [PSCustomObject] -and $Live.$key -is [PSCustomObject]) {
            Merge-MissingKeys -Live $Live.$key -Template $Template.$key
        }
    }
}

Write-Host "Merging new config keys from published defaults..."
$liveConfig     = $appSettingsBackup | ConvertFrom-Json
$templateConfig = Get-Content $AppSettings -Raw | ConvertFrom-Json
Merge-MissingKeys -Live $liveConfig -Template $templateConfig

# Migrate legacy configs that pointed PromptFilePath at the repo working copy.
# The prompt is now deployed into the install directory by dotnet publish.
$repoPrompt     = (Join-Path $ScriptDir "prompts\default.md") -replace '\\', '/'
$deployedPrompt = (Join-Path $InstallDir "prompts\default.md") -replace '\\', '/'
if (($liveConfig.Proxy.PromptFilePath -replace '\\', '/') -eq $repoPrompt) {
    $liveConfig.Proxy.PromptFilePath = $deployedPrompt
    Write-Host "  ~ Proxy.PromptFilePath migrated to deployed copy: $deployedPrompt"
}

$liveConfig | ConvertTo-Json -Depth 10 | Set-Content $AppSettings -Encoding UTF8
Write-Host "Config merge complete."

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
