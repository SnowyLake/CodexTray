param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexMonitor.App\CodexMonitor.App.csproj"
$publishDir = Join-Path $repoRoot "Builds\Release\Publish\win-x64"

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

        Write-Host ""
        Write-Host "Publish completed."
        Write-Host "Executable: $(Join-Path $publishDir 'CodexMonitor.App.exe')"
    }
    finally {
        Pop-Location
    }
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
    Invoke-AppPublish
}
catch {
    Write-Host ""
    Write-Host "Publish failed."
    Write-Host $_.Exception.Message
    $exitCode = 1
}
finally {
    Wait-BeforeExit
}

exit $exitCode
