param([switch]$PrepareOnly, [string]$ShortcutDirectory)
$ErrorActionPreference = 'Stop'
$dashboardRoot = $PSScriptRoot
$dashboardNode = (Get-Command node -ErrorAction Stop).Source
if ([int]((& $dashboardNode --version).TrimStart('v').Split('.')[0]) -lt 24) { throw 'Node.js 24 or newer is required.' }
$companionSource = Join-Path $dashboardRoot 'native\CodexCompanion.cs'
$companionHash = (Get-FileHash -LiteralPath $companionSource -Algorithm SHA256).Hash.Substring(0, 12)
$companionRuntime = Join-Path $dashboardRoot '.codex-usage\companion-runtime'
New-Item -ItemType Directory -Path $companionRuntime -Force | Out-Null
$companionExe = & (Join-Path $dashboardRoot 'Build-NativeCompanion.ps1')
if (-not $companionExe) { $companionExe = Join-Path $companionRuntime "CodexContextCompanion-$companionHash.exe" }
if (-not (Test-Path -LiteralPath $companionExe)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compilerPath /nologo /target:winexe "/out:$companionExe" /reference:System.Web.Extensions.dll $companionSource
    if ($LASTEXITCODE -ne 0) { throw 'Companion compilation failed.' }
}
if ($PrepareOnly) { Write-Output $companionExe; return }
if (-not $ShortcutDirectory) { $ShortcutDirectory = [Environment]::GetFolderPath('Startup') }
$companionShell = New-Object -ComObject WScript.Shell
$companionLink = $companionShell.CreateShortcut((Join-Path $ShortcutDirectory 'Codex Context Companion.lnk'))
$companionLink.TargetPath = $companionExe
$companionLink.Arguments = '"' + $dashboardRoot + '" "' + $dashboardNode + '"'
$companionLink.WorkingDirectory = $dashboardRoot
$companionLink.WindowStyle = 7
$companionLink.Description = 'Start the local context monitor only while official Codex is running.'
$companionLink.Save()
Remove-Item -LiteralPath (Join-Path $dashboardRoot '.codex-usage\companion-disabled') -ErrorAction SilentlyContinue
Start-Process -FilePath $companionExe -ArgumentList @(('"' + $dashboardRoot + '"'), ('"' + $dashboardNode + '"')) -WorkingDirectory $dashboardRoot -WindowStyle Hidden
Write-Output 'Codex companion enabled.'
