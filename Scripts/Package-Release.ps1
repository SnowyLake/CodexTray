param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexTray.App\CodexTray.App.csproj"
$releaseBaseRoot = Join-Path $repoRoot "Builds\Release"
$runtime = "win-x64"
$appFileName = "CodexTray.exe"
. (Join-Path $scriptRoot "Publish-Shared.ps1")

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
$releaseRoot = Join-Path $releaseBaseRoot $normalizedVersion
$packageName = "CodexTray-$normalizedVersion-$runtime.zip"
$stagingDir = Join-Path $releaseRoot "CodexTray-$normalizedVersion-$runtime"
$packagePath = Join-Path $releaseRoot $packageName

function Invoke-ReleasePublish {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-PathWithRetry $stagingDir
    }

    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

    Write-Host "Version: $normalizedVersion"
    Write-Host "Staging: $stagingDir"

    Invoke-CodexTrayPublish -RepoRoot $repoRoot -ProjectPath $projectPath -OutputPath $stagingDir -Title "Release package publish started."
    $appPath = Join-Path $stagingDir $appFileName
    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "Published executable not found: $appPath"
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
    Wait-BeforeExit -NoPause:$NoPause
}

exit $exitCode
