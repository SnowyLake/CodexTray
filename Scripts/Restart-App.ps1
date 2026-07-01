param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexMonitor.App\CodexMonitor.App.csproj"
$publishDir = Join-Path $repoRoot "Builds\Release\Publish\win-x64"
$appPath = Join-Path $publishDir "CodexMonitor.App.exe"

function Stop-RunningApp {
    $processes = Get-Process -Name "CodexMonitor.App" -ErrorAction SilentlyContinue
    if (-not $processes) {
        Write-Host "No running CodexMonitor.App process found."
        return
    }

    foreach ($process in $processes) {
        Write-Host "Stopping process $($process.Id): $($process.Path)"
        Stop-Process -Id $process.Id -Force
    }

    foreach ($process in $processes) {
        try {
            Wait-Process -Id $process.Id -Timeout 10
        }
        catch {
            Write-Host "Process $($process.Id) did not exit before timeout."
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
    }
    finally {
        Pop-Location
    }
}

function Start-PublishedApp {
    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "Published executable not found: $appPath"
    }

    $process = Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
    Write-Host ""
    Write-Host "Started CodexMonitor.App."
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
