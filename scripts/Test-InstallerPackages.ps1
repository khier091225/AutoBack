[CmdletBinding()]
param([string]$InstallerDirectory, [string]$Version = '0.7.0')
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
foreach ($role in @('Host', 'Agent')) {
    $path = Join-Path $directory "AutoBack-$role-$Version-win-x64.msi"
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($checksums -notcontains ('{0}  {1}' -f $hash, [IO.Path]::GetFileName($path))) { throw "$role installer checksum mismatch." }
    $database = $installer.OpenDatabase($path, 0)
    try {
        $properties = @(Read-Rows $database 'SELECT `Property`, `Value` FROM `Property`' @('Property','Value'))
        if (($properties | Where-Object Property -eq 'ProductVersion').Value -ne $Version) { throw "$role installer version mismatch." }
        $files = @(Read-Rows $database 'SELECT `File`, `FileName` FROM `File`' @('Id','Name'))
        foreach ($file in $files) {
            $name = ($file.Name -split '\|')[-1]
            if ($name -match '(?i)client_secret|credentials|\.dat$|\.abkey$|\.autoback-invite$|\.zip$|\.abk$' -or
                ($name.EndsWith('.json') -and $name -ne 'package-files.json' -and -not $name.EndsWith('.deps.json') -and -not $name.EndsWith('.runtimeconfig.json'))) {
                throw "$role installer contains an excluded user data file: $name"
            }
        }
        if (-not ($files | Where-Object Name -match 'AUTOMATION-ROLLOUT.md')) { throw "$role installer is missing its rollout guide." }
        if ($role -eq 'Agent') {
            $service = @(Read-Rows $database 'SELECT `Name`, `StartType`, `StartName`, `Arguments` FROM `ServiceInstall`' @('Name','Start','Account','Arguments'))
            if ($service.Count -ne 1 -or $service[0].Name -ne 'AutoBackAgent' -or $service[0].Start -ne '3' -or
                $service[0].Account -ne 'NT AUTHORITY\LocalService' -or $service[0].Arguments -ne 'run --machine') { throw 'Agent service must be installed stopped/manual under LocalService in machine scope.' }
            $controls = @(Read-Rows $database 'SELECT `Event` FROM `ServiceControl`' @('Event'))
            if (($controls | Where-Object { ([int]$_.Event -band 1) -ne 0 })) { throw 'An unconfigured agent service must not start during installation.' }
            $actions = @(Read-Rows $database 'SELECT `Action` FROM `CustomAction`' @('Action'))
            if (-not ($actions | Where-Object Action -match 'ServiceConfig')) { throw 'Service recovery custom actions are missing.' }
            $shortcuts = @(Read-Rows $database 'SELECT `Arguments` FROM `Shortcut`' @('Arguments'))
            if (-not ($shortcuts | Where-Object Arguments -match '-FoldersOnly')) { throw 'The direct backup-folder setup shortcut is missing.' }
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
