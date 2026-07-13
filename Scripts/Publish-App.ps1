param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexTray.App\CodexTray.App.csproj"
$publishDir = Join-Path $repoRoot "Builds\Output\win-x64"
$appFileName = "CodexTray.exe"
. (Join-Path $scriptRoot "Publish-Shared.ps1")

function Invoke-AppPublish {
    Invoke-CodexTrayPublish -RepoRoot $repoRoot -ProjectPath $projectPath -OutputPath $publishDir -Clean
    Write-Host ""
    Write-Host "Publish completed."
    Write-Host "Executable: $(Join-Path $publishDir $appFileName)"
}

$exitCode = 0
try {
    Invoke-AppPublish
}
catch {
    Write-Host ""
    Write-Host "Publish failed."
    Write-Host $_.Exception.Message
    $exitCode = 1
}
finally {
    Wait-BeforeExit -NoPause:$NoPause
}

exit $exitCode
