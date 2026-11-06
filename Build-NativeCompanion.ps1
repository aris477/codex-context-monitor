param([switch]$Test)
$ErrorActionPreference = 'Stop'
$companionRoot = $PSScriptRoot
$companionRuntime = Join-Path $companionRoot '.codex-usage\companion-runtime'
New-Item -ItemType Directory -Path $companionRuntime -Force | Out-Null
$companionHashes = @('native\CodexCompanion.cpp', 'native\CompanionState.h') | ForEach-Object {
    (Get-FileHash -LiteralPath (Join-Path $companionRoot $_) -Algorithm SHA256).Hash.Substring(0, 10)
}
$companionHash = $companionHashes -join ''
$companionExe = Join-Path $companionRuntime "CodexContextNativeCompanion-$companionHash.exe"
if ($Test) { $companionExe = Join-Path $companionRuntime 'CompanionNativeTests.exe' }
if (-not $Test -and (Test-Path -LiteralPath $companionExe)) { Write-Output $companionExe; return }
$companionVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $companionVsWhere)) { return }
$companionVS = & $companionVsWhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $companionVS) { return }
$companionVars = Join-Path $companionVS 'VC\Auxiliary\Build\vcvars64.bat'
$companionSource = if ($Test) { Join-Path $companionRoot 'test\companion-native.test.cpp' } else { Join-Path $companionRoot 'native\CodexCompanion.cpp' }
$companionSubsystem = if ($Test) { 'CONSOLE' } else { 'WINDOWS' }
$companionBuild = 'call "' + $companionVars + '" >nul && cl /nologo /O2 /MT /EHsc /std:c++17 /W4 /DUNICODE /D_UNICODE /utf-8 "' + $companionSource + '" /Fo:"' + (Join-Path $companionRuntime 'companion.obj') + '" /Fe:"' + $companionExe + '" /link /SUBSYSTEM:' + $companionSubsystem
& $env:ComSpec /d /c $companionBuild | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Native companion compilation failed.' }
if ($Test) { & $companionExe; if ($LASTEXITCODE -ne 0) { throw 'Native companion tests failed.' }; return }
Write-Output $companionExe
