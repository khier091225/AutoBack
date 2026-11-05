[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.7.0', [switch]$SkipPublish,
    [ValidateSet('Both','Host','Agent')][string]$TargetPackage = 'Both')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repo 'artifacts'
$output = Join-Path $artifacts 'installers'
$tools = Join-Path $artifacts 'build-tools\wix'
$wix = Join-Path $tools 'wix.exe'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not (Test-Path -LiteralPath $wix -PathType Leaf)) {
    & dotnet tool install wix --version 4.0.6 --tool-path $tools --allow-roll-forward
    if ($LASTEXITCODE -ne 0) { throw 'WiX tool installation failed.' }
}
& $wix extension add -g WixToolset.Util.wixext/4.0.6
if ($LASTEXITCODE -ne 0) { throw 'WiX service configuration extension is unavailable.' }
$stageRoot = Join-Path $artifacts ('installer-stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stageRoot | Out-Null

function StableId([string]$name) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($name))).Replace('-', '').Substring(0, 30)) }
    finally { $hash.Dispose() }
}
function Add-Node($parent, [string]$name, [hashtable]$attributes, [string]$namespace = 'http://wixtoolset.org/schemas/v4/wxs') {
    $node = $parent.OwnerDocument.CreateElement($name, $namespace)
    foreach ($entry in $attributes.GetEnumerator()) { $node.SetAttribute([string]$entry.Key, [string]$entry.Value) }
    $parent.AppendChild($node) | Out-Null
    return $node
}

$roles = if ($TargetPackage -eq 'Both') { @('Host', 'Agent') } else { @($TargetPackage) }
foreach ($role in $roles) {
    $roleKey = $role.ToLowerInvariant()
    $published = Join-Path $artifacts "$roleKey-win-x64"
    $project = if ($role -eq 'Host') { Join-Path $repo 'AutoBack.csproj' } else { Join-Path $repo 'src\AutoBack.Agent\AutoBack.Agent.csproj' }
    if (-not $SkipPublish) {
        & dotnet publish $project -c Release -r win-x64 --self-contained true -o $published "-p:Version=$Version" --nologo
        if ($LASTEXITCODE -ne 0) { throw "$role publish failed." }
    }
    $stage = Join-Path $stageRoot $role
    New-Item -ItemType Directory -Path $stage | Out-Null
    $main = if ($role -eq 'Host') { 'AutoBack.exe' } else { 'AutoBack.Agent.exe' }
    if (-not (Test-Path -LiteralPath (Join-Path $published $main))) { throw "Missing $main. Publish first." }
    $publishedVersion = (Get-Item -LiteralPath (Join-Path $published $main)).VersionInfo.ProductVersion.Split('+')[0]
    if ($publishedVersion -ne $Version) { throw "$role executable version $publishedVersion does not match MSI version $Version." }
    foreach ($file in Get-ChildItem -LiteralPath $published -File -Recurse) {
        $relative = $file.FullName.Substring($published.TrimEnd('\').Length + 1)
        # Never package user OAuth JSON, protected stores, invitations, recovery bundles or test archives.
        $allowed = $file.Extension -in @('.dll', '.md', '.txt') -or $file.Name -in @($main, 'createdump.exe') -or
            $file.Name.EndsWith('.deps.json') -or $file.Name.EndsWith('.runtimeconfig.json')
        if (-not $allowed -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $target = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    if ($role -eq 'Agent') {
        New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-InstalledAgent.ps1') -Destination (Join-Path $stage 'scripts')
    } else {
        New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Enable-HostFirewall.ps1') -Destination (Join-Path $stage 'scripts')
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-ClientReadiness.ps1') -Destination (Join-Path $stage 'scripts')
    $inventory = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($stage.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [IO.File]::WriteAllText((Join-Path $stage 'package-files.json'), (ConvertTo-Json -InputObject $inventory -Depth 4))
    $xml = [Xml.XmlDocument]::new()
    $root = $xml.CreateElement('Wix', 'http://wixtoolset.org/schemas/v4/wxs'); $xml.AppendChild($root) | Out-Null
    $upgrade = if ($role -eq 'Agent') { 'A1E234D7-10CB-40DF-B6D2-9E04015B6D89' } else { '51E56A73-3520-44E9-8125-70F60F676B77' }
    $package = Add-Node $root Package @{ Name = "AutoBack $role"; Manufacturer = 'AutoBack'; Version = $Version; UpgradeCode = $upgrade; Scope = 'perMachine'; InstallerVersion = '500' }
    Add-Node $package MajorUpgrade @{ DowngradeErrorMessage = 'A newer version of AutoBack is already installed.' } | Out-Null
    Add-Node $package MediaTemplate @{ EmbedCab = 'yes'; CompressionLevel = 'medium' } | Out-Null
    Add-Node $package Launch @{ Condition = 'VersionNT64'; Message = 'AutoBack requires 64-bit Windows.' } | Out-Null
    if ($role -eq 'Agent') {
        $property = Add-Node $package Property @{ Id = 'LEGACY_AUTOBACK_SERVICE' }
        Add-Node $property RegistrySearch @{ Id = 'ExistingAgentService'; Root = 'HKLM'; Key = 'SYSTEM\CurrentControlSet\Services\AutoBackAgent'; Name = 'ImagePath'; Type = 'raw' } | Out-Null
        Add-Node $package Launch @{ Condition = 'NOT LEGACY_AUTOBACK_SERVICE OR Installed OR WIX_UPGRADE_DETECTED'; Message = 'An existing AutoBackAgent service was found. For the first MSI install, unregister the legacy script-installed service first; retain its data. MSI upgrades use the existing product upgrade path.' } | Out-Null
    }
    $standard = Add-Node $package StandardDirectory @{ Id = 'ProgramFiles64Folder' }
    $install = Add-Node $standard Directory @{ Id = 'INSTALLFOLDER'; Name = "AutoBack $role" }
    $directories = @{ '' = $install }
    $feature = Add-Node $package Feature @{ Id = 'Main'; Title = "AutoBack $role"; Level = '1' }
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($stage.Length + 1)
        $directory = [IO.Path]::GetDirectoryName($relative)
        if ($directory -and -not $directories.ContainsKey($directory)) {
            $built = ''; $parentNode = $install
            foreach ($part in $directory.Split('\')) {
                $built = if ($built) { "$built\$part" } else { $part }
                if (-not $directories.ContainsKey($built)) { $directories[$built] = Add-Node $parentNode Directory @{ Id = 'D' + (StableId "$role/$built"); Name = $part } }
                $parentNode = $directories[$built]
            }
        }
        $id = StableId "$role/$relative"
        $guid = [Guid]::new(($id + '00').Insert(20, '-').Insert(16, '-').Insert(12, '-').Insert(8, '-'))
        $component = Add-Node $directories[$directory] Component @{ Id = "C$id"; Guid = $guid.ToString(); Bitness = 'always64' }
        Add-Node $component File @{ Id = "F$id"; Source = $file.FullName; KeyPath = 'yes' } | Out-Null
        Add-Node $feature ComponentRef @{ Id = "C$id" } | Out-Null
        if ($role -eq 'Agent' -and $relative -eq $main) {
            $service = Add-Node $component ServiceInstall @{ Id = 'InstallAgent'; Name = 'AutoBackAgent'; DisplayName = 'AutoBack Agent'; Description = 'AutoBack backup, upload and retention worker'; Type = 'ownProcess'; Start = 'demand'; ErrorControl = 'normal'; Account = 'NT AUTHORITY\LocalService'; Arguments = 'run --machine' }
            Add-Node $service ServiceConfig @{ FirstFailureActionType = 'restart'; SecondFailureActionType = 'restart'; ThirdFailureActionType = 'none'; ResetPeriodInDays = '1'; RestartServiceDelayInSeconds = '60' } 'http://wixtoolset.org/schemas/v4/wxs/util' | Out-Null
            Add-Node $component ServiceControl @{ Id = 'ControlAgent'; Name = 'AutoBackAgent'; Stop = 'both'; Remove = 'uninstall'; Wait = 'yes' } | Out-Null
        }
    }
    $menu = Add-Node $package StandardDirectory @{ Id = 'ProgramMenuFolder' }
    $menuFolder = Add-Node $menu Directory @{ Id = 'MenuFolder'; Name = "AutoBack $role" }
    $menuId = StableId "$role/menu"
    $shortcut = Add-Node $menuFolder Component @{ Id = 'MenuComponent'; Guid = ([Guid]::new(($menuId + '00').Insert(20, '-').Insert(16, '-').Insert(12, '-').Insert(8, '-'))).ToString(); Bitness = 'always64' }
    if ($role -eq 'Host') {
        Add-Node $shortcut Shortcut @{ Id = 'LaunchHost'; Name = 'AutoBack Host'; Target = ('[#F' + (StableId "$role/$main") + ']'); WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
    } else {
        Add-Node $shortcut Shortcut @{ Id = 'SetupAgent'; Name = 'Configure AutoBack Agent'; Target = '[System64Folder]WindowsPowerShell\v1.0\powershell.exe'; Arguments = '-NoProfile -File "[INSTALLFOLDER]scripts\Configure-InstalledAgent.ps1"'; WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
        Add-Node $shortcut Shortcut @{ Id = 'ChooseBackupFolders'; Name = 'Choose AutoBack backup folders'; Target = '[System64Folder]WindowsPowerShell\v1.0\powershell.exe'; Arguments = '-NoProfile -File "[INSTALLFOLDER]scripts\Configure-InstalledAgent.ps1" -FoldersOnly'; WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
    }
    Add-Node $shortcut Shortcut @{ Id = 'ReadGuide'; Name = 'AutoBack rollout guide'; Target = '[System64Folder]notepad.exe'; Arguments = '"[INSTALLFOLDER]docs\AUTOMATION-ROLLOUT.md"'; WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
    Add-Node $shortcut RemoveFolder @{ Id = 'RemoveMenuFolder'; On = 'uninstall' } | Out-Null
    Add-Node $shortcut RegistryValue @{ Root = 'HKLM'; Key = "Software\AutoBack\$role"; Name = 'InstallDirectory'; Value = '[INSTALLFOLDER]'; Type = 'string'; KeyPath = 'yes' } | Out-Null
    Add-Node $feature ComponentRef @{ Id = 'MenuComponent' } | Out-Null
    $wxs = Join-Path $stageRoot "$role.wxs"; $xml.Save($wxs)
    $msi = Join-Path $output "AutoBack-$role-$Version-win-x64.msi"
    & $wix build $wxs -arch x64 -ext WixToolset.Util.wixext -out $msi -pdbtype none
    if ($LASTEXITCODE -ne 0) { throw "$role MSI build failed." }
    Write-Output "Built $msi"
}
$checksums = @(Get-ChildItem -LiteralPath $output -Filter "AutoBack-*-$Version-win-x64.msi" | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name })
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $checksums)
Write-Output 'MSIs built. No installation or service changes were performed. Verify them on pilot PCs using docs\ROLLOUT-CHECKLIST.md.'
