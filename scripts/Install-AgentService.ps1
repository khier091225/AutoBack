#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishedAgentDirectory,
    [Parameter(Mandatory = $true)][string]$InvitationPath,
    [string]$ComputerName = $env:COMPUTERNAME,
    [switch]$ReplacePairing
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PublishedAgentDirectory).ProviderPath
$invitation = (Resolve-Path -LiteralPath $InvitationPath).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $source 'AutoBack.Agent.exe') -PathType Leaf)) { throw 'Select the published agent directory containing AutoBack.Agent.exe and its dependencies.' }
if (Get-Service -Name 'AutoBackAgent' -ErrorAction SilentlyContinue) { throw 'AutoBackAgent is already installed. Stop/unregister it before reinstalling.' }

# The executable runs from Program Files, so ordinary users cannot replace service code.
$destination = Join-Path $env:ProgramFiles 'AutoBack Agent'
if ([IO.Path]::GetFullPath($source).TrimEnd('\') -eq [IO.Path]::GetFullPath($destination).TrimEnd('\')) { throw 'Publish to a separate source directory first.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    if ($file.Extension -notin @('.dll', '.md', '.txt') -and $file.Name -notin @('AutoBack.Agent.exe', 'createdump.exe') -and
        -not $file.Name.EndsWith('.deps.json') -and -not $file.Name.EndsWith('.runtimeconfig.json')) { continue }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Published service files cannot be links.' }
    $relative = $file.FullName.Substring($source.TrimEnd('\').Length + 1)
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
$agent = Join-Path $destination 'AutoBack.Agent.exe'
$pairArguments = @('pair', '--machine', '--invite', $invitation, '--name', $ComputerName)
if ($ReplacePairing) { $pairArguments += '--replace' }
& $agent @pairArguments
if ($LASTEXITCODE -ne 0) { throw 'Pairing failed. The service has not been registered. Create a fresh invitation and retry.' }

$serviceAccount = [Management.Automation.PSCredential]::new('NT AUTHORITY\LocalService', [Security.SecureString]::new())
$binaryPath = '"{0}" run --machine' -f $agent
New-Service -Name 'AutoBackAgent' -DisplayName 'AutoBack Agent' -Description 'Authenticated AutoBack client connection and heartbeat service.' -BinaryPathName $binaryPath -StartupType Automatic -Credential $serviceAccount | Out-Null
& sc.exe failure AutoBackAgent reset= 86400 actions= restart/60000/restart/60000/none/0
if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
if (-not [Diagnostics.EventLog]::SourceExists('AutoBack.Agent')) { New-EventLog -LogName Application -Source 'AutoBack.Agent' }
Start-Service -Name 'AutoBackAgent'
Write-Output 'AutoBack Agent installed with automatic startup under LocalService. Check the host dashboard for live heartbeats.'
