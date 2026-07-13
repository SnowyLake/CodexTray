param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexTray.App\CodexTray.App.csproj"
$publishDir = Join-Path $repoRoot "Builds\Output\win-x64"
$appProcessNames = @("CodexTray", "CodexTray.App")
$appPath = Join-Path $publishDir "CodexTray.exe"
. (Join-Path $scriptRoot "Publish-Shared.ps1")

function Stop-RunningApp {
    $processes = foreach ($appProcessName in $appProcessNames) {
        Get-Process -Name $appProcessName -ErrorAction SilentlyContinue
    }
    if (-not $processes) {
        Write-Host "No running CodexTray process found."
        return
    }

    foreach ($process in $processes) {
        Write-Host "Stopping process $($process.Id): $($process.Path)"
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
            if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
                Write-Host "Process $($process.Id) is still running after timeout."
            }
            else {
                Write-Host "Process $($process.Id) stopped."
            }
        }
        catch {
            Write-Host "Process $($process.Id) could not be stopped: $($_.Exception.Message)"
        }
    }
}

function Remove-PreviousPublishDirectory {
    if (Test-Path -LiteralPath $publishDir) {
        Remove-PathWithRetry $publishDir
        Write-Host "Removed previous publish directory."
    }
}

function Invoke-AppPublish {
    Invoke-CodexTrayPublish -RepoRoot $repoRoot -ProjectPath $projectPath -OutputPath $publishDir
}

function Start-PublishedApp {
    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "Published executable not found: $appPath"
    }

    $process = Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
    Write-Host ""
    Write-Host "Started CodexTray."
    Write-Host "Process: $($process.Id)"
    Write-Host "Path:    $appPath"
}

$exitCode = 0
try {
    Stop-RunningApp
    Remove-PreviousPublishDirectory
    Invoke-AppPublish
    Start-PublishedApp
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
