$ErrorActionPreference = 'Stop'
$companionData = Join-Path $PSScriptRoot '.codex-usage'
New-Item -ItemType Directory -Path $companionData -Force | Out-Null
Set-Content -LiteralPath (Join-Path $companionData 'companion-disabled') -Value 'disabled' -Encoding ASCII
$companionStartupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex Context Companion.lnk'
if (Test-Path -LiteralPath $companionStartupLink) {
    $companionShell = New-Object -ComObject WScript.Shell
    $companionExisting = $companionShell.CreateShortcut($companionStartupLink)
    if ($companionExisting.WorkingDirectory -eq $PSScriptRoot) { Remove-Item -LiteralPath $companionStartupLink }
}
Write-Output 'Companion disabled. It will exit within 15 seconds. The dashboard can still be started manually.'
