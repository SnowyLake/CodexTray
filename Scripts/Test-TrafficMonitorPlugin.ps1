param(
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $repoRoot "Plugins\TrafficMonitor"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $pluginRoot "Builds\Tests"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio Build Tools with the C++ workload are required."
}
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsPath)) {
    throw "Visual Studio Build Tools with the C++ workload were not found."
}
$vsDevCmd = Join-Path $vsPath "Common7\Tools\VsDevCmd.bat"

& (Join-Path $PSScriptRoot "Build-TrafficMonitorPlugin.ps1")
$testRoot = Join-Path $OutputRoot "TrafficMonitorPluginTests"
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$testRoot = (Resolve-Path -LiteralPath $testRoot).Path
$testSource = Join-Path $pluginRoot "Tests\TrafficMonitorPluginTests.cpp"
$testObject = Join-Path $testRoot "TrafficMonitorPluginTests.obj"
$testExe = Join-Path $testRoot "TrafficMonitorPluginTests.exe"
$testDll = Join-Path $testRoot "CodexTray.dll"
Copy-Item -LiteralPath (Join-Path $pluginRoot "Builds\x64\Release\CodexTray.dll") -Destination $testDll -Force

$previousVsDevCmd = [Environment]::GetEnvironmentVariable("CODEXTRAY_VSDEV_CMD", "Process")
$env:CODEXTRAY_VSDEV_CMD = $vsDevCmd
$env:Path = "$(Split-Path -Parent $vswhere);$env:Path"
try {
    $vsEnvironment = & $env:ComSpec /d /c 'call "%CODEXTRAY_VSDEV_CMD%" -arch=amd64 >nul && set'
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to initialize the Visual C++ environment."
    }
    foreach ($entry in $vsEnvironment) {
        $separator = $entry.IndexOf('=')
        if ($separator -gt 0) {
            [Environment]::SetEnvironmentVariable($entry.Substring(0, $separator), $entry.Substring($separator + 1), "Process")
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable("CODEXTRAY_VSDEV_CMD", $previousVsDevCmd, "Process")
}

$compileArgs = @("/nologo", "/std:c++17", "/EHsc", "/MD", "/utf-8", "/W3", $testSource, "/Fo$testObject", "/Fe$testExe")
& cl.exe @compileArgs
if ($LASTEXITCODE -ne 0) {
    throw "TrafficMonitor plugin test compilation failed with exit code $LASTEXITCODE."
}

& $testExe $testDll
if ($LASTEXITCODE -ne 0) {
    throw "TrafficMonitor plugin tests failed with exit code $LASTEXITCODE."
}
