#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FileFlow'),
    [switch]$DeleteConfigurationAndLogs
)
$ErrorActionPreference = 'Stop'
$serviceName = 'FileFlowService'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    & sc.exe delete $serviceName | Out-Host
}
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'FileFlowManager' -ErrorAction SilentlyContinue
Write-Host 'Exit the FileFlow tray application before deleting its program folder.'
if (Test-Path $InstallDir) { Remove-Item -Path $InstallDir -Recurse -Force }
if ($DeleteConfigurationAndLogs) {
    $data = Join-Path $env:ProgramData 'FileFlow'
    if (Test-Path $data) { Remove-Item -Path $data -Recurse -Force }
    Write-Host 'Configuration and logs deleted.'
} else {
    Write-Host 'Configuration and logs retained under ProgramData\FileFlow.'
}
