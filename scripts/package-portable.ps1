#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string] $Arch,
    [Parameter(Mandatory)][string] $PublishFolder,
    [Parameter(Mandatory)][string] $OutputFolder,
    # For CI test builds, e.g. "test.57": goes in the zip's name so it isn't taken for a release.
    [ValidatePattern('^[a-z0-9.]*$')][string] $Label = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$windows = Split-Path $PSScriptRoot -Parent
$launcher = Join-Path $windows 'launcher'
$expectedVersion = (Get-Content -LiteralPath (Join-Path $windows 'VERSION') -Raw).Trim()
if ($Version -cnotmatch '^\d+\.\d+\.\d+$' -or $Version -cne $expectedVersion) {
    throw "Version must match VERSION ($expectedVersion)"
}
$parts = @($Version.Split('.') | ForEach-Object { [uint16]::Parse($_) })
$Arch = $Arch.ToLowerInvariant()
$publish = (Resolve-Path -LiteralPath $PublishFolder).Path
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Lyricod.exe') -PathType Leaf)) {
    throw "No Lyricod.exe in publish folder: $publish"
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFolder)
New-Item -ItemType Directory -Path $output -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw "vswhere not found: $vswhere" }
$components = @('Microsoft.VisualStudio.Component.VC.Tools.x86.x64')
$vcTarget = 'x64'
if ($Arch -eq 'arm64') {
    $components += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
    $vcTarget = 'x64_arm64'
}
$installation = & $vswhere -latest -products '*' -requires @components -property installationPath
if ($LASTEXITCODE -ne 0) { throw "vswhere failed ($LASTEXITCODE)" }
if (-not $installation) { throw "No Visual Studio installation with MSVC for $Arch" }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvarsall.bat'
if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf)) { throw "vcvarsall not found: $vcvars" }

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("Lyricod-portable-" + [guid]::NewGuid())
$build = Join-Path $temporary 'build'
$stage = Join-Path $temporary 'Lyricod'
$app = Join-Path $stage 'app'
$originalEnvironment = @{}
Get-ChildItem Env: | ForEach-Object { $originalEnvironment[$_.Name] = $_.Value }
try {
    New-Item -ItemType Directory -Path $build, $app | Out-Null

    # Import vcvarsall from cmd, splitting only at the first '=' (PATH and other values may contain it).
    $environmentScript = Join-Path $build 'environment.cmd'
    @"
@echo off
call "$vcvars" $vcTarget >nul
if errorlevel 1 exit /b %errorlevel%
set
"@ | Set-Content -LiteralPath $environmentScript -Encoding ascii
    $environment = & $env:ComSpec /d /c "call `"$environmentScript`""
    if ($LASTEXITCODE -ne 0) { throw "vcvarsall failed for $vcTarget ($LASTEXITCODE)" }
    foreach ($line in $environment) {
        if ($line -match '^([^=]+)=(.*)$') {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
        }
    }

    # RC reads the version from this generated header; VERSION remains the source.
    @"
#define LYRICOD_VERSION $($parts -join ','),0
#define LYRICOD_VERSION_STRING "$Version"
"@ | Set-Content -LiteralPath (Join-Path $build 'version.h') -Encoding ascii

    $resource = Join-Path $build 'launcher.res'
    $object = Join-Path $build 'launcher.obj'
    $executable = Join-Path $stage 'Lyricod.exe'
    Push-Location $launcher
    try {
        & rc.exe /nologo /i $build /fo $resource launcher.rc
        if ($LASTEXITCODE -ne 0) { throw "Resource compilation failed ($LASTEXITCODE)" }
        & cl.exe /nologo /std:c++17 /utf-8 /W4 /WX /O1 /GL /MT /EHsc /DUNICODE /D_UNICODE `
            /DWIN32_LEAN_AND_MEAN /DNOMINMAX "/Fo$object" "/Fe$executable" main.cpp $resource `
            /link /SUBSYSTEM:WINDOWS /MANIFEST:NO /LTCG /OPT:REF /OPT:ICF kernel32.lib user32.lib
        if ($LASTEXITCODE -ne 0) { throw "Launcher compilation failed for $Arch ($LASTEXITCODE)" }
    }
    finally {
        Pop-Location
    }

    Copy-Item -LiteralPath (Join-Path $launcher 'README.txt') -Destination $stage
    Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $app -Recurse -Force

    $entries = @(Get-ChildItem -LiteralPath $stage -Force)
    $expectedEntries = @('Lyricod.exe', 'README.txt', 'app')
    if ($entries.Count -ne 3 -or @(Compare-Object $expectedEntries $entries.Name).Count -ne 0) {
        throw 'Portable folder must contain exactly Lyricod.exe, README.txt and app'
    }
    foreach ($file in @($executable, (Join-Path $stage 'README.txt'), (Join-Path $app 'Lyricod.exe'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing staged file: $file" }
    }
    if (-not (Test-Path -LiteralPath $app -PathType Container)) { throw 'Missing staged app folder' }
    $allowedCultures = @('en-us', 'zh-CN', 'zh-TW')
    $unexpectedCultures = @(Get-ChildItem -LiteralPath $app -Directory -Force | Where-Object {
        $_.Name -match '^[a-z]{2,3}(?:-[a-z0-9]{2,8})*$' -and $_.Name -notin $allowedCultures
    })
    if ($unexpectedCultures.Count -gt 0) {
        throw "Unexpected language folders: $($unexpectedCultures.Name -join ', ')"
    }

    $name = if ($Label) { "Lyricod-$Version-$Label-windows-$Arch.zip" } else { "Lyricod-$Version-windows-$Arch.zip" }
    $zip = Join-Path $output $name
    # Include the Lyricod directory itself, and preserve every published file, including hidden ones.
    Add-Type -AssemblyName System.IO.Compression.ZipFile
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "Created $zip"
}
finally {
    # The release workflow builds both architectures in the same PowerShell process.
    Get-ChildItem Env: | Where-Object { -not $originalEnvironment.ContainsKey($_.Name) } | ForEach-Object {
        [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process')
    }
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
