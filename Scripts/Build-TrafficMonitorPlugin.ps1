param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$pluginRoot = Join-Path $repoRoot "Plugins\TrafficMonitor"
$projectPath = Join-Path $pluginRoot "TrafficMonitorPlugin.vcxproj"

function Get-MSBuildPath {
    $command = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswherePath = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswherePath) {
        $installationPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installationPath)) {
            $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }

    throw "MSBuild was not found. Install Visual Studio Build Tools with the C++ workload."
}

$msbuildPath = Get-MSBuildPath
Write-Host "Building TrafficMonitor plugin."
Write-Host "Project: $projectPath"
Write-Host "MSBuild: $msbuildPath"

& $msbuildPath $projectPath /m /p:Configuration=$Configuration /p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    throw "TrafficMonitor plugin build failed with exit code $LASTEXITCODE."
}

$outputPath = Join-Path $pluginRoot "Builds\$Platform\$Configuration\CodexTray.dll"
Write-Host "Plugin DLL: $outputPath"
