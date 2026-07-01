param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexMonitor.App\CodexMonitor.App.csproj"
$releaseRoot = Join-Path $repoRoot "Builds\Release"
$stagingRoot = Join-Path $releaseRoot "Package"
$runtime = "win-x64"
$appFileName = "CodexMonitor.exe"

function Get-NormalizedVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RawVersion
    )

    $value = $RawVersion.Trim()
    if ($value.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(1)
    }

    if ($value -notmatch "^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$") {
        throw "Version must look like X.Y.Z, for example 0.1.0 or v0.1.0."
    }

    return "v$value"
}

$normalizedVersion = Get-NormalizedVersion $Version
$packageName = "CodexMonitor-$normalizedVersion-$runtime.zip"
$stagingDir = Join-Path $stagingRoot "CodexMonitor-$normalizedVersion-$runtime"
$packagePath = Join-Path $releaseRoot $packageName

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

function Invoke-ReleasePublish {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-PathWithRetry $stagingDir
    }

    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

    Write-Host "Release package publish started."
    Write-Host "Version: $normalizedVersion"
    Write-Host "Project: $projectPath"
    Write-Host "Staging: $stagingDir"

    Push-Location $repoRoot
    try {
        dotnet publish $projectPath -c Release -f net9.0-windows -r $runtime -p:PublishSingleFile=true -p:SelfContained=false -o $stagingDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }

        Get-ChildItem -LiteralPath $stagingDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
            Remove-Item -Force

        $appPath = Join-Path $stagingDir $appFileName
        if (-not (Test-Path -LiteralPath $appPath)) {
            throw "Published executable not found: $appPath"
        }
    }
    finally {
        Pop-Location
    }
}

function New-ReleaseZip {
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }

    Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $packagePath -CompressionLevel Optimal

    Write-Host ""
    Write-Host "Release package created."
    Write-Host "Package: $packagePath"
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
    Invoke-ReleasePublish
    New-ReleaseZip
}
catch {
    Write-Host ""
    Write-Host "Release package failed."
    Write-Host $_.Exception.Message
    $exitCode = 1
}
finally {
    Wait-BeforeExit
}

exit $exitCode
