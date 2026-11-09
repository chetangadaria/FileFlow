# Requires Windows with .NET 10 SDK and Visual Studio 2026 (or equivalent build tools).
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root 'publish'
New-Item -ItemType Directory -Path $publish -Force | Out-Null

Write-Host 'Running cross-platform core smoke tests...'
dotnet run --project (Join-Path $root 'tests\FileFlow.Core.SmokeTests\FileFlow.Core.SmokeTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }

Write-Host 'Publishing 64-bit self-contained Windows Service...'
dotnet publish (Join-Path $root 'src\FileFlow.Service\FileFlow.Service.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $publish 'Service')
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

Write-Host 'Publishing 64-bit self-contained tray Manager...'
dotnet publish (Join-Path $root 'src\FileFlow.Manager\FileFlow.Manager.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $publish 'Manager')
if ($LASTEXITCODE -ne 0) { throw 'Manager publish failed.' }

Write-Host "Published successfully to $publish"
Write-Host 'Next: run scripts\Install-FileFlow.ps1 from an elevated PowerShell session.'
