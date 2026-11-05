[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$HostAddress, [ValidateRange(1,65535)][int]$Port = 47831,
    [string]$AgentPath = (Join-Path $env:ProgramFiles 'AutoBack Agent\AutoBack.Agent.exe'), [string]$ReportPath)
$ErrorActionPreference = 'Stop'
$reachable = $false; $client = [Net.Sockets.TcpClient]::new()
try {
    $attempt = $client.BeginConnect($HostAddress, $Port, $null, $null)
    if ($attempt.AsyncWaitHandle.WaitOne(5000)) { $client.EndConnect($attempt); $reachable = $client.Connected }
} catch [Net.Sockets.SocketException] { $reachable = $false }
finally { $client.Dispose() }
$agent = Get-Item -LiteralPath $AgentPath -ErrorAction SilentlyContinue
$service = Get-CimInstance Win32_Service -Filter "Name='AutoBackAgent'" -ErrorAction SilentlyContinue
$report = [ordered]@{
    checkedAtUtc = [DateTime]::UtcNow.ToString('O'); computer = $env:COMPUTERNAME
    windowsVersion = [Environment]::OSVersion.Version.ToString(); x64 = [Environment]::Is64BitOperatingSystem
    hostAddress = $HostAddress; port = $Port; tcpReachable = $reachable
    agentInstalled = [bool]$agent; agentVersion = $(if ($agent) { $agent.VersionInfo.ProductVersion } else { $null })
    serviceState = $(if ($service) { $service.State } else { 'Not installed' })
    serviceAccount = $(if ($service) { $service.StartName } else { $null })
    serviceStartMode = $(if ($service) { $service.StartMode } else { $null })
    notes = 'Readiness only. TCP reachability does not prove TLS pairing, source permissions, Google access, a successful backup or usable recovery. Complete docs/ROLLOUT-CHECKLIST.md.'
}
if (-not $ReportPath) { $ReportPath = Join-Path (Get-Location) ('AutoBack-readiness-' + $env:COMPUTERNAME + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '.json') }
if (Test-Path -LiteralPath $ReportPath) { throw 'Choose a new report path; existing reports are preserved.' }
$json = ConvertTo-Json -InputObject $report -Depth 4
$stream = [IO.FileStream]::new([IO.Path]::GetFullPath($ReportPath), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $bytes = [Text.Encoding]::UTF8.GetBytes($json); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
Write-Output $json
if (-not $reachable -or -not $agent -or -not [Environment]::Is64BitOperatingSystem) { exit 1 }
