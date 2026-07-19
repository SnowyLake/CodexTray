param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$publishDir = Join-Path $repoRoot "Builds\Output\win-x64"
$appPath = Join-Path $publishDir "CodexTray.exe"
. (Join-Path $scriptRoot "Publish-Shared.ps1")

$exitCode = 0
try {
    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "Published executable not found: $appPath"
    }

    Stop-CodexTrayApp
    Start-CodexTrayApp -AppPath $appPath
}
catch {
    Write-Host ""
    Write-Host "Restart failed."
    Write-Host $_.Exception.Message
    $exitCode = 1
}
finally {
    Wait-BeforeExit -NoPause:$NoPause
}

exit $exitCode
