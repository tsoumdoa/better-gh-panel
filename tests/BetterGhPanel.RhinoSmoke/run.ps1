param(
    [string]$PluginPath,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 180,
    [string[]]$Fixture = @()
)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$PluginPath) { $PluginPath = Join-Path $taskRoot 'src/BetterGhPanel/bin/Release/net7.0/BetterGhPanel.gha' }
$PluginPath = [System.IO.Path]::GetFullPath($PluginPath)
& dotnet build (Join-Path $PSScriptRoot 'BetterGhPanel.RhinoSmoke.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$taskArgs = @((Join-Path $PSScriptRoot 'coordinator.mjs'), '--plugin', $PluginPath, '--timeout-ms', ($TimeoutSeconds * 1000).ToString())
foreach ($taskFixture in $Fixture) { $taskArgs += @('--fixture', $taskFixture) }
& node @taskArgs
exit $LASTEXITCODE
