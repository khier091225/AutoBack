#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name 'AutoBackAgent' -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name 'AutoBackAgent'
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    & sc.exe delete AutoBackAgent
    if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
}
Write-Output 'Service unregistered. Published files and protected pairing data are retained. Revoke the pairing in the host if retiring this computer.'
