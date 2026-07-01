param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexMonitor.App\CodexMonitor.App.csproj"
$publishDir = Join-Path $repoRoot "Builds\Release\Publish\win-x64"
$appProcessNames = @("CodexMonitor", "CodexMonitor.App")
$appPath = Join-Path $publishDir "CodexMonitor.exe"

function Stop-RunningApp {
    $processes = foreach ($appProcessName in $appProcessNames) {
        Get-Process -Name $appProcessName -ErrorAction SilentlyContinue
    }
    if (-not $processes) {
        Write-Host "No running CodexMonitor process found."
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

function Remove-PathWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 10) {
                throw
            }

            Start-Sleep -Milliseconds 500
        }
    }
}

function Invoke-AppPublish {
    Write-Host "CodexMonitor publish started."
    Write-Host "Project: $projectPath"
    Write-Host "Output:  $publishDir"
    Push-Location $repoRoot
    try {
        dotnet publish $projectPath -c Release -f net9.0-windows -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -o $publishDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }

        Remove-PublishDebugSymbols
    }
    finally {
        Pop-Location
    }
}

function Remove-PublishDebugSymbols {
    Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

function Start-PublishedApp {
    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "Published executable not found: $appPath"
    }

    $process = Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
    Write-Host ""
    Write-Host "Started CodexMonitor."
    Write-Host "Process: $($process.Id)"
    Write-Host "Path:    $appPath"
}

function Wait-BeforeExit {
    if ($NoPause) {
        return
    }

    Write-Host ""
    Write-Host "Press any key to close this window..."
    [Console]::ReadKey($true) | Out-Null
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
    Wait-BeforeExit
}

exit $exitCode
