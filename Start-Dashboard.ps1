param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$dashboardRoot = $PSScriptRoot
$dashboardPort = if ($env:CODEX_CONTEXT_PORT) { [int]$env:CODEX_CONTEXT_PORT } else { 3765 }
$dashboardUrl = "http://127.0.0.1:$dashboardPort"
$dashboardNode = (Get-Command node -ErrorAction Stop).Source
Remove-Item -LiteralPath (Join-Path $dashboardRoot '.codex-usage\companion-paused') -ErrorAction SilentlyContinue
if ([int]((& $dashboardNode --version).TrimStart('v').Split('.')[0]) -lt 24) {
    throw 'Please install Node.js 24 or newer.'
}
$dashboardReady = $false
try {
    $dashboardCheck = Invoke-RestMethod -Uri "$dashboardUrl/api/monitor-health" -TimeoutSec 2
    $dashboardReady = $dashboardCheck.app -eq 'codex-context-monitor'
} catch { }
if (-not $dashboardReady) {
    & $dashboardNode (Join-Path $dashboardRoot 'start-local.js')
    # Wait for the HTTP listener, rather than opening a connection-refused page.
    for ($dashboardAttempt = 0; $dashboardAttempt -lt 40; $dashboardAttempt++) {
        try {
            $dashboardCheck = Invoke-RestMethod -Uri "$dashboardUrl/api/monitor-health" -TimeoutSec 1
            $dashboardReady = $dashboardCheck.app -eq 'codex-context-monitor'
            if ($dashboardReady) { break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $dashboardReady) { throw 'Dashboard did not start. See dashboard-error.log.' }
}
if (-not $NoBrowser) { Start-Process $dashboardUrl }
Write-Host "Dashboard: $dashboardUrl"
