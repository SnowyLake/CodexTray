param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexTray.App\CodexTray.App.csproj"
$publishDir = Join-Path $repoRoot "Builds\Output\win-x64"
$appPath = Join-Path $publishDir "CodexTray.exe"
. (Join-Path $scriptRoot "Publish-Shared.ps1")

function Invoke-AppPublish {
    Stop-CodexTrayApp
    Invoke-CodexTrayPublish -RepoRoot $repoRoot -ProjectPath $projectPath -OutputPath $publishDir -Clean
    Start-CodexTrayApp -AppPath $appPath
    Write-Host ""
    Write-Host "Publish completed."
    Write-Host "Executable: $appPath"
}

$exitCode = 0
try {
    Invoke-AppPublish
}
catch {
    Write-Host ""
    Write-Host "Publish workflow failed."
    Write-Host $_.Exception.Message
    $exitCode = 1
}
finally {
    Wait-BeforeExit -NoPause:$NoPause
}

exit $exitCode
