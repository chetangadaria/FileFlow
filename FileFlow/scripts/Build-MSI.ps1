# Windows-only, builds self-contained Windows x64 executables and a Windows Installer MSI.
# Requires: Windows + .NET 10 SDK + WiX Toolset MSBuild SDK 7.0.1 (NuGet restore).
[CmdletBinding()]
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'
$wixproj = Join-Path $root 'installer\FileFlow.Setup.wixproj'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'Build-MSI.ps1 must run on Windows with the .NET 10 SDK.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found. Install the .NET 10 SDK on Windows first.'
}
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'Build-Release.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Publishing FileFlow executables failed.' }
}
foreach ($path in @(
    (Join-Path $publish 'Service\FileFlow.Service.exe'),
    (Join-Path $publish 'Manager\FileFlow.Manager.exe')))
{
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Missing published payload: $path. Run without -SkipPublish."
    }
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host 'Compiling WiX v7 MSI project, including complete self-contained win-x64 payloads...'
& dotnet build $wixproj -c Release -p:OutputPath=$dist
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed (review NuGet/WiX restore and validation errors).' }

$msi = Join-Path $dist 'FileFlow-Server-x64.msi'
if (-not (Test-Path $msi -PathType Leaf)) {
    throw "The WiX build completed, but expected MSI not found: $msi"
}
$hash = Get-FileHash $msi -Algorithm SHA256
Write-Host "MSI built: $msi"
Write-Host "SHA256: $($hash.Hash)"
Write-Host 'Install with elevated PowerShell: msiexec.exe /i "FileFlow-Server-x64.msi" /l*v "FileFlow-install.log"'
Write-Warning 'Code-sign the MSI and validate install/uninstall on Server 2022 and 2025 before production use.'
