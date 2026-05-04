#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the Claude Code Proxy as a Windows service.

.DESCRIPTION
    Verifies .NET 10 prerequisites, builds the proxy from source, publishes
    binaries to %LOCALAPPDATA%\ClaudeCodeProxy, and registers a Windows
    service. The custom prompt file remains in this source repository
    (prompts\default.md) so it can be edited under version control —
    install.ps1 rewrites the installed appsettings.json's PromptFilePath
    to the absolute path of the in-repo prompt.

    On re-install, the existing installed appsettings.json is preserved
    so user-configured changes survive.

    The service is registered and started automatically.
#>

$ErrorActionPreference = "Stop"

# --- Prereq check ---
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Error "dotnet CLI not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}

$sdks = dotnet --list-sdks 2>&1
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Error ".NET 10 SDK not found.`nInstall from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}

$runtimes = dotnet --list-runtimes 2>&1
if (-not ($runtimes | Where-Object { $_ -match 'Microsoft\.AspNetCore\.App 10\.' })) {
    Write-Error "ASP.NET Core 10 runtime not found.`nInstall from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}

Write-Host ".NET 10 SDK and ASP.NET Core 10 runtime verified."

# --- Paths ---
$ScriptDir   = $PSScriptRoot
$ProjectDir  = Join-Path $ScriptDir "src\ClaudeCodeProxy"
$InstallDir  = Join-Path $env:LOCALAPPDATA "ClaudeCodeProxy"
$PromptPath  = Join-Path $ScriptDir "prompts\default.md"
$DumpPath    = Join-Path $InstallDir "dumps"
$ExePath     = Join-Path $InstallDir "ClaudeCodeProxy.exe"
$AppSettings = Join-Path $InstallDir "appsettings.json"

$ServiceName = "ClaudeCodeProxy"
$DisplayName = "Claude Code Proxy"
$Description = "Transparent HTTP proxy for Claude Code (Vertex AI). Replaces the static system-prompt portion with a custom one."

# Verify the in-repo prompt file exists before going further
if (-not (Test-Path $PromptPath)) {
    Write-Error "Prompt file not found at $PromptPath. Did you clone the repo cleanly?"
    exit 1
}

# --- Preserve existing user appsettings (re-install case) ---
$existingAppSettings = $null
if (Test-Path $AppSettings) {
    $existingAppSettings = Get-Content $AppSettings -Raw
    Write-Host "Existing appsettings.json found — will preserve user changes."
}

# --- Stop and remove existing service BEFORE publishing ---
# (dotnet publish would fail to overwrite ClaudeCodeProxy.exe if the service
#  is still running and holding a file lock.)
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    if ($existingService.Status -eq 'Running') {
        Write-Host "Stopping existing service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Removing existing service '$ServiceName'..."
    sc.exe delete $ServiceName | Out-Null

    # Wait for the SCM to actually release the service (sc.exe delete is async).
    $timeout = 15
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and $timeout -gt 0) {
        Start-Sleep -Seconds 1
        $timeout--
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Error "Service '$ServiceName' is still present after delete (may be marked for deletion). Close any open handles or reboot, then re-run."
        exit 1
    }
}

# --- Create install directory ---
$StatsPath   = Join-Path $InstallDir "stats"
foreach ($dir in @($InstallDir, $DumpPath, $StatsPath)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "Created: $dir"
    }
}

# --- Build / publish ---
Write-Host "Building..."
dotnet publish $ProjectDir --configuration Release --runtime win-x64 --self-contained false --output $InstallDir
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }
Write-Host "Build complete."

# --- Restore or initialize appsettings ---
if ($existingAppSettings) {
    Set-Content $AppSettings -Value $existingAppSettings -Encoding UTF8
    Write-Host "User appsettings.json restored."
} else {
    # First install: rewrite prompt + dump paths to install-relative absolutes
    $json = Get-Content $AppSettings -Raw | ConvertFrom-Json
    $json.Proxy.PromptFilePath  = ($PromptPath -replace '\\', '/')
    $json.Dump.OutputPath       = ($DumpPath   -replace '\\', '/')
    $json.Stats.OutputPath      = ($StatsPath  -replace '\\', '/')
    $json | ConvertTo-Json -Depth 20 | Set-Content $AppSettings -Encoding UTF8
    Write-Host "Default appsettings.json installed:"
    Write-Host "  Proxy.PromptFilePath = $PromptPath"
    Write-Host "  Dump.OutputPath      = $DumpPath"
    Write-Host "  Stats.OutputPath     = $StatsPath"
}

# --- Register service ---
Write-Host "Registering service '$ServiceName'..."
sc.exe create $ServiceName binPath= "`"$ExePath`"" DisplayName= "$DisplayName" start= auto
if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create failed."; exit 1 }
sc.exe description $ServiceName "$Description"

# --- Start the service ---
Write-Host "Starting service '$ServiceName'..."
try {
    Start-Service -Name $ServiceName -ErrorAction Stop
    $svc = Get-Service -Name $ServiceName
    Write-Host "Service '$ServiceName' is $($svc.Status)."
} catch {
    Write-Warning "Service registered but failed to start: $_"
    Write-Warning "Try: Start-Service $ServiceName"
}

Write-Host ""
Write-Host "  Install dir:  $InstallDir"
Write-Host "  Prompt file:  $PromptPath  (edit in source repo)"
Write-Host "  Settings:     $AppSettings"
Write-Host "  Dumps:        $DumpPath"
Write-Host "  Logs:         $InstallDir\logs\proxy_YYYYMMDD.log"
Write-Host ""
Write-Host "Next: in your claude shell, set ANTHROPIC_VERTEX_BASE_URL=http://127.0.0.1:9996"
