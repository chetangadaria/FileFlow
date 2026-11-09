#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$PublishRoot = (Join-Path $PSScriptRoot '..\publish'),
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FileFlow'),
    [switch]$PromptForServiceCredential,
    [switch]$NoTrayAutostart
)
$ErrorActionPreference = 'Stop'
$serviceName = 'FileFlowService'
$serviceExe = Join-Path $InstallDir 'Service\FileFlow.Service.exe'
$managerExe = Join-Path $InstallDir 'Manager\FileFlow.Manager.exe'

if (-not (Test-Path (Join-Path $PublishRoot 'Service\FileFlow.Service.exe'))) {
    throw 'Published Service executable not found. First run .\scripts\Build-Release.ps1.'
}
if (-not (Test-Path (Join-Path $PublishRoot 'Manager\FileFlow.Manager.exe'))) {
    throw 'Published Manager executable not found. First run .\scripts\Build-Release.ps1.'
}

$credential = $null
if ($PromptForServiceCredential) {
    $credential = Get-Credential -Message 'Service account that can access local/UNC folders'
    if ($null -eq $credential) { throw 'A service account was not provided.' }
}

$previous = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($previous) {
    if ($previous.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    & sc.exe delete $serviceName | Out-Host
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Path (Join-Path $InstallDir 'Service') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $InstallDir 'Manager') -Force | Out-Null
Copy-Item -Path (Join-Path $PublishRoot 'Service\*') -Destination (Join-Path $InstallDir 'Service') -Recurse -Force
Copy-Item -Path (Join-Path $PublishRoot 'Manager\*') -Destination (Join-Path $InstallDir 'Manager') -Recurse -Force

$dataRoot = Join-Path $env:ProgramData 'FileFlow'
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Logs') -Force | Out-Null
# Restrict configuration writes: SYSTEM/Admins full; normal Users read.
& icacls.exe $dataRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Failed to set configuration directory ACL.' }
if ($credential) {
    $account = $credential.UserName
    & icacls.exe $dataRoot /grant "${account}:(OI)(CI)M" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant ProgramData access to $account." }
}

$args = @{
    Name = $serviceName
    BinaryPathName = '"' + $serviceExe + '"'
    DisplayName = 'FileFlow Automated File Transfer'
    Description = 'Copies or moves configured local/UNC folders every five seconds; daily logs and SNMP traps.'
    StartupType = 'Automatic'
}
if ($credential) { $args['Credential'] = $credential }
New-Service @args | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Failed to set service recovery actions.' }
Start-Service -Name $serviceName

$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
if (-not $NoTrayAutostart) {
    New-ItemProperty -Path $runKey -Name 'FileFlowManager' -Value ('"' + $managerExe + '" --tray') `
        -PropertyType String -Force | Out-Null
    Write-Host 'The tray manager will start automatically at interactive sign-in.'
} else {
    Remove-ItemProperty -Path $runKey -Name 'FileFlowManager' -ErrorAction SilentlyContinue
}

Write-Host "Installed $serviceName (Automatic start)."
Write-Host "Task data and logs: $dataRoot"
Write-Host "Manager: $managerExe"
if (-not $credential) {
    Write-Warning 'Service runs as LocalSystem. UNC shares often require a domain service account or gMSA.'
}
Write-Warning 'Run Manager as administrator to SAVE task configuration. Standard users can monitor only.'
