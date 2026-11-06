[CmdletBinding()]
param([switch]$FoldersOnly, [switch]$PairAgain, [switch]$ReconnectGoogle)
$ErrorActionPreference = 'Stop'
if (([int]$FoldersOnly.IsPresent + [int]$PairAgain.IsPresent + [int]$ReconnectGoogle.IsPresent) -gt 1) { throw 'Choose one setup action: folders, pairing recovery, or Google sign-in.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # This shortcut is an interactive setup tool: the operator must see folder pickers and sign-in.
    $elevatedArguments = @('-NoProfile', '-File', ('"{0}"' -f $PSCommandPath))
    if ($FoldersOnly) { $elevatedArguments += '-FoldersOnly' }
    if ($PairAgain) { $elevatedArguments += '-PairAgain' }
    if ($ReconnectGoogle) { $elevatedArguments += '-ReconnectGoogle' }
    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -ArgumentList $elevatedArguments
    exit
}
$agent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\AutoBack.Agent.exe'))
if (-not (Test-Path -LiteralPath $agent -PathType Leaf)) { throw 'The installed agent executable is missing.' }
$service = Get-Service -Name AutoBackAgent -ErrorAction Stop
$wasRunning = $service.Status -eq 'Running'
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name AutoBackAgent
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
}
if ($FoldersOnly) { & $agent configure --machine } elseif ($PairAgain) { & $agent re-pair --machine } elseif ($ReconnectGoogle) { & $agent drive-connect --machine } else { & $agent setup --machine }
if ($LASTEXITCODE -eq 2) {
    if ($wasRunning) { Start-Service -Name AutoBackAgent }
    Write-Output 'Setup cancelled. A previously running service was restarted with the saved configuration.'
    Read-Host 'Press Enter to close'
    exit 0
}
if ($LASTEXITCODE -ne 0) { throw 'Client setup was cancelled or failed. Saved pairing/configuration is retained; run setup again.' }
$destinationMode = & $agent destination-status --machine
if ($LASTEXITCODE -ne 0) { throw 'Could not read the saved backup destination mode.' }
if ($destinationMode -eq 'GoogleDrive') {
    & $agent drive-ensure --machine
    if ($LASTEXITCODE -ne 0) { throw 'Google Drive sign-in did not complete. Service remains stopped; rerun setup to connect.' }
}
& $agent access-check --machine
if ($LASTEXITCODE -ne 0) { throw 'Backup access is not ready. Open Choose AutoBack backup folders, use Grant required access, and resolve all listed issues before starting.' }
$answer = Read-Host 'Backup access check passed. Type START to start the service, or press Enter to leave it stopped'
if ($answer -ceq 'START') {
    & sc.exe config AutoBackAgent start= delayed-auto
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable automatic service startup.' }
    Start-Service -Name AutoBackAgent
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    Write-Output 'Agent service started. Confirm the new client and a real backup/restore in the host dashboard.'
} else { Write-Output 'Service left stopped. After permissions are ready, run Start-Service AutoBackAgent as administrator.' }
Read-Host 'Press Enter to close'
