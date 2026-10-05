param([string]$PluginPath)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$PluginPath) { $PluginPath = Join-Path $taskRoot 'src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.gha' }
$PluginPath = [System.IO.Path]::GetFullPath($PluginPath)
$taskResult = Join-Path $PSScriptRoot 'bin/Release/net7.0-windows/host-result.json'
$taskLog = Join-Path $PSScriptRoot 'bin/Release/net7.0-windows/host-output.log'
& dotnet build (Join-Path $PSScriptRoot 'BetterGhPanel.RhinoSmoke.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (Test-Path -LiteralPath $taskResult) { Remove-Item -LiteralPath $taskResult }
$env:PANEL_SMOKE_RESULT = $taskResult
try {
    & (Join-Path $PSScriptRoot 'bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe') --run $PluginPath 2>&1 | Tee-Object -FilePath $taskLog
    $taskNativeExit = $LASTEXITCODE
} finally { Remove-Item Env:PANEL_SMOKE_RESULT }
if (!(Test-Path -LiteralPath $taskResult)) { throw "Rhino host did not produce a completed smoke result (native exit $taskNativeExit). See $taskLog" }
$taskReport = Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json
if (!$taskReport.passed) { throw "Native smoke assertions failed (native exit $taskNativeExit). See $taskLog" }
if ($taskNativeExit -ne 0) { throw "Assertions completed but native host shutdown failed (exit $taskNativeExit). See $taskLog" }
Write-Host "Verified native assertions passed; Rhino shutdown exit was $taskNativeExit. Canvas drawing verified: $($taskReport.canvasDrawingVerified)."
exit 0
