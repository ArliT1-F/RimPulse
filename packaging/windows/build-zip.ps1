<#
.SYNOPSIS
Build a portable Windows ZIP from a self-contained `dotnet publish` output.

.DESCRIPTION
Windows packaging is prepared but not exercised in CI on Linux. This script produces a portable ZIP;
`installer.iss` is an Inno Setup 6 script for a real installer, and must be compiled on Windows with
ISCC.exe. Neither is a substitute for installing and running RimPulse on a Windows desktop.

.EXAMPLE
dotnet publish src/RimPulse.App -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
pwsh packaging/windows/build-zip.ps1 -PublishDir artifacts/publish/win-x64 -Version 0.7.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PublishDir,
    [string] $Version = '0.0.0-dev',
    [string] $Runtime = 'win-x64',
    [string] $OutputDir = ''
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9][0-9A-Za-z.+~-]*$') {
    throw "Invalid version '$Version'. Use e.g. 0.7.0."
}
if (-not $OutputDir) {
    $OutputDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts/packages'
}

$publish = (Resolve-Path -LiteralPath $PublishDir).Path
foreach ($required in @('RimPulse.exe', 'RimPulse.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) {
        throw "Publish directory is missing '$required'; run dotnet publish -r $Runtime --self-contained true first."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $publish 'RimPulse.deps.json') -PathType Leaf) -and
    -not (Test-Path -LiteralPath (Join-Path $publish 'RimPulse.runtimeconfig.json') -PathType Leaf)) {
    throw "Publish directory is missing the dependency/runtime manifests; run dotnet publish first."
}

$stagingParent = Join-Path ([System.IO.Path]::GetTempPath()) ("rimpulse-win-" + [guid]::NewGuid().ToString('N'))
$staging = Join-Path $stagingParent ("RimPulse-$Version-$Runtime")
New-Item -ItemType Directory -Path $staging -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$zip = Join-Path $OutputDir "RimPulse-$Version-$Runtime.zip"

try {
    # -Path (not -LiteralPath) so the trailing '*' expands to the publish directory's contents.
    Copy-Item -Path (Join-Path $publish '*') -Destination $staging -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'rimpulse.ico') -Destination (Join-Path $staging 'rimpulse.ico') -Force

    @"
RimPulse $Version — portable Windows build ($Runtime)

Run RimPulse.exe from this folder. No installer, registry entries or system-wide
changes are made; RimPulse keeps its configuration and logs under %APPDATA% and
%LOCALAPPDATA%. A RimWorld 1.6 or 1.7 installation is still required for
mod builds and development launches.

This is an unsigned build. Windows SmartScreen may warn before the first run.
The upstream repository does not publish a LICENSE file yet; do not redistribute
this archive until a license is chosen. See packaging/README.md.
"@ | Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Encoding utf8

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
    Write-Host "Wrote $zip"
    Write-Host "SHA256 $hash"
}
finally {
    if (Test-Path -LiteralPath $stagingParent) { Remove-Item -LiteralPath $stagingParent -Recurse -Force }
}
