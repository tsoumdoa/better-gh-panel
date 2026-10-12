param([string]$PluginPath)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$PluginPath) { $PluginPath = Join-Path $taskRoot 'src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.gha' }
$PluginPath = [System.IO.Path]::GetFullPath($PluginPath)
& dotnet build (Join-Path $PSScriptRoot 'BetterGhPanel.RhinoSmoke.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# A second DrawToBitmap fixture crashes Rhino 8.35's hidden display pipeline.
# Run both native captures in fresh hosts; the full command always includes both.
foreach ($taskMode in @('--run', '--item-order-capture')) {
    $taskSuffix = if ($taskMode -eq '--run') { '' } else { '-item-order' }
    $taskResult = Join-Path $PSScriptRoot "bin/Release/net7.0-windows/host$taskSuffix-result.json"
    $taskLog = Join-Path $PSScriptRoot "bin/Release/net7.0-windows/host$taskSuffix-output.log"
    if (Test-Path -LiteralPath $taskResult) { Remove-Item -LiteralPath $taskResult }
    $env:PANEL_SMOKE_RESULT = $taskResult
    try {
        & (Join-Path $PSScriptRoot 'bin/Release/net7.0-windows/BetterGhPanel.RhinoSmoke.exe') $taskMode $PluginPath 2>&1 | Tee-Object -FilePath $taskLog
        $taskNativeExit = $LASTEXITCODE
    } finally { Remove-Item Env:PANEL_SMOKE_RESULT }
    if (!(Test-Path -LiteralPath $taskResult)) { throw "Rhino host did not produce a completed smoke result (native exit $taskNativeExit). See $taskLog" }
    $taskReport = Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json
    if (!$taskReport.passed) { throw "Native smoke assertions failed (native exit $taskNativeExit). See $taskLog" }
    if ($taskNativeExit -ne 0) { throw "Assertions completed but native host shutdown failed (exit $taskNativeExit). See $taskLog" }
    Write-Host "Verified $taskMode assertions passed; Rhino shutdown exit was $taskNativeExit. Canvas drawing verified: $($taskReport.canvasDrawingVerified)."
}
exit 0
