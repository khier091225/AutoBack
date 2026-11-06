[CmdletBinding()]
param([string]$InstallerDirectory, [string]$Version = '0.8.1', [switch]$IncludeLegacy)
$ErrorActionPreference = 'Stop'
if (-not $InstallerDirectory) { $InstallerDirectory = Join-Path $PSScriptRoot '..\artifacts\installers' }
$directory = [IO.Path]::GetFullPath($InstallerDirectory)
$installer = New-Object -ComObject WindowsInstaller.Installer
function Invoke-Com($object, [string]$name, [object[]]$arguments) {
    return $object.GetType().InvokeMember($name, [Reflection.BindingFlags]::InvokeMethod, $null, $object, $arguments)
}
function Read-Rows($database, [string]$query, [string[]]$columns) {
    $view = Invoke-Com $database OpenView @($query)
    try {
        Invoke-Com $view Execute @() | Out-Null
        while ($record = Invoke-Com $view Fetch @()) {
            $values = [ordered]@{}
            for ($index = 0; $index -lt $columns.Count; $index++) {
                $values[$columns[$index]] = $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($index + 1))
            }
            [pscustomobject]$values
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        }
    } finally { Invoke-Com $view Close @() | Out-Null; [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
}
$checksums = Get-Content -LiteralPath (Join-Path $directory 'SHA256SUMS.txt')
$results = @()
$roles = @('Host', 'Agent')
if ($IncludeLegacy) { $roles += 'Agent-Legacy' }
foreach ($role in $roles) {
    $isLegacy = $role -eq 'Agent-Legacy'
    $architecture = if ($isLegacy) { 'x86' } else { 'x64' }
    $path = Join-Path $directory "AutoBack-$role-$Version-win-$architecture.msi"
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($checksums -notcontains ('{0}  {1}' -f $hash, [IO.Path]::GetFileName($path))) { throw "$role installer checksum mismatch." }
    $database = $installer.OpenDatabase($path, 0)
    try {
        $properties = @(Read-Rows $database 'SELECT `Property`, `Value` FROM `Property`' @('Property','Value'))
        if (($properties | Where-Object Property -eq 'ProductVersion').Value -ne $Version) { throw "$role installer version mismatch." }
        $summary = $database.SummaryInformation(0)
        $template = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(7))
        if (($isLegacy -and $template -notmatch '^Intel;') -or (-not $isLegacy -and $template -notmatch '^x64;')) { throw "$role MSI architecture mismatch: $template" }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) | Out-Null
        $files = @(Read-Rows $database 'SELECT `File`, `FileName` FROM `File`' @('Id','Name'))
        foreach ($file in $files) {
            $name = ($file.Name -split '\|')[-1]
            if ($name -match '(?i)client_secret|credentials|\.dat$|\.abkey$|\.autoback-invite$|\.zip$|\.abk$' -or
                ($name.EndsWith('.json') -and $name -ne 'package-files.json' -and -not $name.EndsWith('.deps.json') -and -not $name.EndsWith('.runtimeconfig.json'))) {
                throw "$role installer contains an excluded user data file: $name"
            }
        }
        if (-not ($files | Where-Object Name -match 'AUTOMATION-ROLLOUT.md')) { throw "$role installer is missing its rollout guide." }
        if ($role -ne 'Host') {
            $service = @(Read-Rows $database 'SELECT `Name`, `StartType`, `StartName`, `Arguments` FROM `ServiceInstall`' @('Name','Start','Account','Arguments'))
            if ($service.Count -ne 1 -or $service[0].Name -ne 'AutoBackAgent' -or $service[0].Start -ne '3' -or
                $service[0].Account -ne 'NT AUTHORITY\LocalService' -or $service[0].Arguments -ne 'run --machine') { throw 'Agent service must be installed stopped/manual under LocalService in machine scope.' }
            $controls = @(Read-Rows $database 'SELECT `Event` FROM `ServiceControl`' @('Event'))
            if (($controls | Where-Object { ([int]$_.Event -band 1) -ne 0 })) { throw 'An unconfigured agent service must not start during installation.' }
            $actions = @(Read-Rows $database 'SELECT `Action` FROM `CustomAction`' @('Action'))
            if (-not ($actions | Where-Object Action -match 'ServiceConfig')) { throw 'Service recovery custom actions are missing.' }
            $shortcuts = @(Read-Rows $database 'SELECT `Arguments` FROM `Shortcut`' @('Arguments'))
            $folderArgument = if ($isLegacy) { 'installed-setup --folders-only' } else { '-FoldersOnly' }
            if (-not ($shortcuts | Where-Object Arguments -match ([regex]::Escape($folderArgument)))) { throw 'The direct backup-folder setup shortcut is missing.' }
            $pairArgument = if ($isLegacy) { 'installed-setup --pair-again' } else { '-PairAgain' }
            if (-not ($shortcuts | Where-Object Arguments -match ([regex]::Escape($pairArgument)))) { throw 'The pairing recovery shortcut is missing.' }
            if ($isLegacy) {
                if (-not ($files | Where-Object Name -match 'AutoBack.Agent.exe.config') -or -not ($files | Where-Object Name -match 'WINDOWS7-CLIENT.md') -or
                    -not ($files | Where-Object Name -match 'BouncyCastle.Cryptography.dll') -or ($files | Where-Object Name -match 'coreclr.dll|hostfxr.dll')) { throw 'Legacy Framework configuration/runtime inventory mismatch.' }
                $conditions = @(Read-Rows $database 'SELECT `Condition` FROM `LaunchCondition`' @('Condition'))
                if (-not ($conditions | Where-Object Condition -match 'NETFRAMEWORK_RELEASE >= "#528040"') -or -not ($conditions | Where-Object Condition -match 'ServicePackLevel >= 1')) { throw 'Legacy .NET Framework 4.8 / Windows 7 SP1 checks missing.' }
                $components = @(Read-Rows $database 'SELECT `Attributes` FROM `Component`' @('Attributes'))
                if ($components | Where-Object { ([int]$_.Attributes -band 256) -ne 0 }) { throw 'Legacy MSI contains 64-bit components.' }
                if ($shortcuts | Where-Object Arguments -match 'powershell|Configure-InstalledAgent.ps1') { throw 'Legacy setup must use the native launcher.' }
                # Open a disposable evaluation session only. Never execute MSI actions or install the product.
                $session = $installer.OpenPackage($path, 1)
                try {
                    $frameworkCondition = ($conditions | Where-Object Condition -match 'NETFRAMEWORK_RELEASE').Condition
                    $session.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @('Installed', '')) | Out-Null
                    foreach ($case in @(@('', 0), @('#461808', 0), @('#528049', 1), @('#533320', 1))) {
                        $session.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @('NETFRAMEWORK_RELEASE', $case[0])) | Out-Null
                        if ((Invoke-Com $session EvaluateCondition @($frameworkCondition)) -ne $case[1]) { throw 'Legacy Framework prerequisite condition evaluates incorrectly.' }
                    }
                    $spCondition = ($conditions | Where-Object Condition -match 'ServicePackLevel >= 1').Condition
                    $session.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @('VersionNT', '601')) | Out-Null
                    foreach ($case in @(@('0', 0), @('1', 1))) {
                        $session.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @('ServicePackLevel', $case[0])) | Out-Null
                        if ((Invoke-Com $session EvaluateCondition @($spCondition)) -ne $case[1]) { throw 'Legacy Windows 7 SP1 prerequisite condition evaluates incorrectly.' }
                    }
                } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($session) | Out-Null }
            }
        }
        $results += [pscustomobject]@{ role = $role; version = $Version; fileCount = $files.Count; sha256 = $hash; inspected = $true; installed = $false }
        Write-Output "PASS $role MSI: version, checksum, runtime inventory, guide and credential exclusions checked."
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null }
}
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $scriptTokens = $null; $scriptErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$scriptTokens, [ref]$scriptErrors) | Out-Null
    if ($scriptErrors.Count -gt 0) { throw ('PowerShell syntax errors in ' + $script.Name + ': ' + ($scriptErrors | Out-String)) }
}
$report = [ordered]@{ verifiedAtUtc = [DateTime]::UtcNow.ToString('O'); packages = $results; allScriptsParsed = $true; serviceInstallAndPhysicalPilot = 'Pending target PCs; no local product installation performed' }
[IO.File]::WriteAllText((Join-Path $directory 'package-validation.json'), (ConvertTo-Json -InputObject $report -Depth 5))
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
Write-Output 'ALL PACKAGE INSPECTION CHECKS PASSED. Actual install/upgrade/service/physical-PC checks remain in docs\ROLLOUT-CHECKLIST.md.'
