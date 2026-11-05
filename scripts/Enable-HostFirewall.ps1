#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$HostExecutable)
$ErrorActionPreference = 'Stop'
$program = (Resolve-Path -LiteralPath $HostExecutable).ProviderPath
if ([IO.Path]::GetFileName($program) -ne 'AutoBack.exe') { throw 'Select the AutoBack host executable.' }
if (Get-NetFirewallRule -Name 'AutoBack-Host-47831' -ErrorAction SilentlyContinue) { throw 'The AutoBack firewall rule already exists. Inspect it before changing its executable path.' }
New-NetFirewallRule -Name 'AutoBack-Host-47831' -DisplayName 'AutoBack host (LAN)' -Program $program -Direction Inbound -Action Allow -Protocol TCP -LocalPort 47831 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
Write-Output 'Allowed AutoBack host TCP 47831 from the local subnet on Private/Domain networks.'
