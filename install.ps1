#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptDir "src\ClaudeCodeProxy"
$publishDir = Join-Path $scriptDir "publish"
$exePath    = Join-Path $publishDir "ClaudeCodeProxy.exe"

$serviceName = "ClaudeCodeProxy"
$displayName = "Claude Code Proxy"
$description = "Transparent HTTP proxy for Claude Code. Intercepts POST /v1/messages and replaces the system prompt before forwarding to api.anthropic.com."

Write-Host "Publishing..."
dotnet publish "$projectDir" -c Release -o "$publishDir" --nologo

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service..."
sc.exe create $serviceName binPath= "`"$exePath`"" DisplayName= "`"$displayName`"" start= auto
sc.exe description $serviceName "`"$description`""

Write-Host ""
Write-Host "Done. Service '$serviceName' created (not yet started)."
Write-Host "Start with:  Start-Service $serviceName"
Write-Host "Stop with:   Stop-Service $serviceName"
Write-Host "Remove with: sc.exe delete $serviceName"
Write-Host ""
Write-Host "Configure prompt file in: $publishDir\appsettings.json"
Write-Host "  Set Proxy.PromptFilePath to the path of your custom prompt .md file"
Write-Host ""
Write-Host "Point Claude Code at the proxy:"
Write-Host "  set ANTHROPIC_BASE_URL=http://127.0.0.1:9996"
