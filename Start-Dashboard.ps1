$ErrorActionPreference = 'Stop'
$dashboardRoot = $PSScriptRoot
$dashboardPort = if ($env:CODEX_CONTEXT_PORT) { [int]$env:CODEX_CONTEXT_PORT } else { 3765 }
$dashboardUrl = "http://127.0.0.1:$dashboardPort"
$dashboardNode = (Get-Command node -ErrorAction Stop).Source
if ((& $dashboardNode -p 'Number(process.versions.node.split(".")[0])') -lt 24) {
    throw 'Please install Node.js 24 or newer.'
}
$dashboardReady = $false
try {
    $dashboardCheck = Invoke-RestMethod -Uri "$dashboardUrl/api/monitor-health" -TimeoutSec 2
    $dashboardReady = $dashboardCheck.app -eq 'codex-context-monitor'
} catch { }
if (-not $dashboardReady) {
    Start-Process -FilePath $dashboardNode -ArgumentList @('--no-warnings', ('"' + (Join-Path $dashboardRoot 'local-server.js') + '"')) -WorkingDirectory $dashboardRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dashboardRoot 'dashboard.log') -RedirectStandardError (Join-Path $dashboardRoot 'dashboard-error.log')
    # The UI can display indexing progress while the first scan runs.
    Start-Sleep -Milliseconds 1000
}
Start-Process $dashboardUrl
Write-Host "Dashboard: $dashboardUrl"
