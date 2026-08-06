function Publish-ClaudeCodeSessionNameMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SessionId,
        [Parameter(Mandatory)][string]$SessionName,
        [Parameter(Mandatory)][string]$Cwd
    )

    if ([string]::IsNullOrWhiteSpace($SessionId) -or [string]::IsNullOrWhiteSpace($SessionName)) { return }

    $client = $null
    $content = $null
    $response = $null
    $cacheStream = $null
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($SessionId)
        $key = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $tempRoot = if ([string]::IsNullOrWhiteSpace($env:TEMP)) { [IO.Path]::GetTempPath() } else { $env:TEMP }
        $cacheDir = Join-Path $tempRoot 'ClaudeCodeProxy\session-metadata'
        $cachePath = Join-Path $cacheDir "$key.last-published"
        $now = [DateTimeOffset]::UtcNow
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

        # This exclusive lock preserves the thirty-second limit even if status-line runs overlap.
        $cacheStream = [IO.File]::Open($cachePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $reader = [IO.StreamReader]::new($cacheStream, [Text.Encoding]::ASCII, $false, 1024, $true)
        $lastPublishedText = $reader.ReadToEnd()
        $reader.Dispose()
        if (-not [string]::IsNullOrWhiteSpace($lastPublishedText)) {
            $lastPublished = [DateTimeOffset]::Parse($lastPublishedText, [Globalization.CultureInfo]::InvariantCulture)
            if (($now - $lastPublished).TotalSeconds -lt 30) { return }
        }

        # Record the attempted publish first. A proxy outage is at most one one-second request per interval.
        $cacheStream.SetLength(0)
        $writer = [IO.StreamWriter]::new($cacheStream, [Text.Encoding]::ASCII, 1024, $true)
        $writer.Write($now.ToString('O'))
        $writer.Flush()
        $writer.Dispose()
        $cacheStream.Flush($true)
        $cacheStream.Dispose()
        $cacheStream = $null

        $payload = [ordered]@{
            event = 'status-line'
            session_id = $SessionId
            session_name = $SessionName
            cwd = $Cwd
            observed_at = $now.ToString('O')
            source = 'claude-code-status-line'
        } | ConvertTo-Json -Compress
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(1)
        $content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
        $response = $client.PostAsync('http://127.0.0.1:9996/internal/session-metadata', $content).GetAwaiter().GetResult()
    }
    catch {
        # Strictly best effort: metadata must never affect Claude Code or the visible status line.
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $content) { $content.Dispose() }
        if ($null -ne $client) { $client.Dispose() }
        if ($null -ne $cacheStream) { $cacheStream.Dispose() }
    }
}
