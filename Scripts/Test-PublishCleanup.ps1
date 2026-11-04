$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Publish-Shared.ps1")

# Exercise the real cleanup without building an application into the fixture.
function dotnet {
    $global:LASTEXITCODE = 0
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot "CodexTray-publish-cleanup-$([Guid]::NewGuid().ToString('N'))"))
if (-not $fixtureRoot.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Cleanup fixture must remain inside the temporary directory."
}

$settingsFiles = @{
    "settings.json" = "damaged main configuration"
    "settings.last-good.json" = "valid backup configuration"
    "settings.damaged-test.json" = "preserved original configuration"
    "cursor-usage-cache.json" = "cached Cursor usage"
    "cursor-usage-cache.json.gz" = "compressed Cursor usage"
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    foreach ($name in $settingsFiles.Keys) {
        [IO.File]::WriteAllText((Join-Path $fixtureRoot $name), $settingsFiles[$name])
    }

    [IO.File]::WriteAllText((Join-Path $fixtureRoot "CodexTray.exe"), "old executable")
    $resourceRoot = Join-Path $fixtureRoot "Resources"
    [IO.Directory]::CreateDirectory($resourceRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $resourceRoot "old-resource.txt"), "old resource")
    Invoke-CodexTrayPublish -RepoRoot $PSScriptRoot -ProjectPath "unused.csproj" -OutputPath $fixtureRoot -Clean

    foreach ($name in $settingsFiles.Keys) {
        if ([IO.File]::ReadAllText((Join-Path $fixtureRoot $name)) -cne $settingsFiles[$name]) {
            throw "Publish cleanup changed $name."
        }
    }

    if ((Test-Path -LiteralPath (Join-Path $fixtureRoot "CodexTray.exe")) -or (Test-Path -LiteralPath $resourceRoot)) {
        throw "Publish cleanup retained obsolete application files."
    }

    Write-Host "PASS publish cleanup preserves settings, backups, preserved originals, and usage cache."
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

# Mock process commands so path isolation is tested without stopping real applications.
& {
    $previewPath = Join-Path $fixtureRoot "CodexTray.exe"
    $fixtureProcesses = @([pscustomobject]@{ Id = 101; Path = $previewPath })
    $stopped = [Collections.Generic.List[int]]::new()
    function Get-Process {
        param($Name, $ErrorAction)
        if ($Name -eq "CodexTray") { $fixtureProcesses }
    }
    function Stop-Process {
        param($Id, [switch]$Force, $ErrorAction)
        $stopped.Add($Id)
    }
    function Wait-Process { param($Id, $ErrorAction) }

    Stop-CodexTrayApp -AppPath $previewPath
    if ($stopped.Count -ne 1 -or $stopped[0] -ne 101) { throw "Preview process was not stopped." }
    $stopped.Clear()
    $fixtureProcesses += [pscustomobject]@{ Id = 102; Path = (Join-Path $fixtureRoot "other\CodexTray.exe") }
    $rejected = $false
    try { Stop-CodexTrayApp -AppPath $previewPath }
    catch { $rejected = $_.Exception.Message -like "Another CodexTray installation*" }
    if (-not $rejected -or $stopped.Count -ne 0) { throw "Other installations must be rejected before stopping any process." }
    Write-Host "PASS preview restart isolates processes by executable path."
}
