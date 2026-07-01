param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexMonitor.App\CodexMonitor.App.csproj"
$publishDir = Join-Path $repoRoot "Builds\Release\Publish\win-x64"
$appFileName = "CodexMonitor.exe"

function Invoke-AppPublish {
    Write-Host "CodexMonitor publish started."
    Write-Host "Project: $projectPath"
    Write-Host "Output:  $publishDir"
    if (Test-Path -LiteralPath $publishDir) {
        Remove-PathWithRetry $publishDir
        Write-Host "Removed previous publish directory."
    }

    Push-Location $repoRoot
    try {
        dotnet publish $projectPath -c Release -f net9.0-windows -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -o $publishDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }

        Remove-PublishDebugSymbols
        Write-Host ""
        Write-Host "Publish completed."
        Write-Host "Executable: $(Join-Path $publishDir $appFileName)"
    }
    finally {
        Pop-Location
    }
}

function Remove-PublishDebugSymbols {
    Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
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
