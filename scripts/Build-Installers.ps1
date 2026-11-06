[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.8.0', [switch]$SkipPublish,
    [ValidateSet('Both','Host','Agent','AgentLegacy')][string]$TargetPackage = 'Both')
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
    $isLegacy = $role -eq 'AgentLegacy'
    $isAgent = $role -ne 'Host'
    $architecture = if ($isLegacy) { 'x86' } else { 'x64' }
    $roleKey = $role.ToLowerInvariant()
    $published = if ($isLegacy) { Join-Path $artifacts 'agent-win7-x86' } else { Join-Path $artifacts "$roleKey-win-x64" }
    $project = if ($role -eq 'Host') { Join-Path $repo 'AutoBack.csproj' } elseif ($isLegacy) { Join-Path $repo 'src\AutoBack.Agent.Legacy\AutoBack.Agent.Legacy.csproj' } else { Join-Path $repo 'src\AutoBack.Agent\AutoBack.Agent.csproj' }
    if (-not $SkipPublish) {
        if ($isLegacy) { & dotnet build $project -c Release "-p:Version=$Version" --nologo; if ($LASTEXITCODE -eq 0) { & dotnet publish $project -c Release --no-build -o $published "-p:Version=$Version" --nologo } }
        else { & dotnet publish $project -c Release -r win-x64 --self-contained true -o $published "-p:Version=$Version" --nologo }
        if ($LASTEXITCODE -ne 0) { throw "$role publish failed." }
    }
    $stage = Join-Path $stageRoot $role
    New-Item -ItemType Directory -Path $stage | Out-Null
    $main = if ($isAgent) { 'AutoBack.Agent.exe' } else { 'AutoBack.exe' }
    if (-not (Test-Path -LiteralPath (Join-Path $published $main))) { throw "Missing $main. Publish first." }
    $publishedVersion = (Get-Item -LiteralPath (Join-Path $published $main)).VersionInfo.ProductVersion.Split('+')[0]
    if ($publishedVersion -ne $Version) { throw "$role executable version $publishedVersion does not match MSI version $Version." }
    foreach ($file in Get-ChildItem -LiteralPath $published -File -Recurse) {
        $relative = $file.FullName.Substring($published.TrimEnd('\').Length + 1)
        # Never package user OAuth JSON, protected stores, invitations, recovery bundles or test archives.
        $allowed = $file.Extension -in @('.dll', '.md', '.txt') -or $file.Name -in @($main, 'createdump.exe', "$main.config") -or
            $file.Name.EndsWith('.deps.json') -or $file.Name.EndsWith('.runtimeconfig.json')
        if (-not $allowed -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $target = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    if ($isAgent -and -not $isLegacy) {
        New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-InstalledAgent.ps1') -Destination (Join-Path $stage 'scripts')
    } elseif (-not $isAgent) {
        New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Enable-HostFirewall.ps1') -Destination (Join-Path $stage 'scripts')
    }
    if (-not $isLegacy) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-ClientReadiness.ps1') -Destination (Join-Path $stage 'scripts') }
    $inventory = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($stage.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [IO.File]::WriteAllText((Join-Path $stage 'package-files.json'), (ConvertTo-Json -InputObject $inventory -Depth 4))
    $xml = [Xml.XmlDocument]::new()
    $root = $xml.CreateElement('Wix', 'http://wixtoolset.org/schemas/v4/wxs'); $xml.AppendChild($root) | Out-Null
    $upgrade = if ($isLegacy) { 'C98C5214-A71C-4D21-997A-226BCEFB0558' } elseif ($isAgent) { 'A1E234D7-10CB-40DF-B6D2-9E04015B6D89' } else { '51E56A73-3520-44E9-8125-70F60F676B77' }
    $displayRole = if ($isLegacy) { 'Agent Legacy' } else { $role }
    $package = Add-Node $root Package @{ Name = "AutoBack $displayRole"; Manufacturer = 'AutoBack'; Version = $Version; UpgradeCode = $upgrade; Scope = 'perMachine'; InstallerVersion = '500' }
    Add-Node $package MajorUpgrade @{ DowngradeErrorMessage = 'A newer version of AutoBack is already installed.' } | Out-Null
    Add-Node $package MediaTemplate @{ EmbedCab = 'yes'; CompressionLevel = 'medium' } | Out-Null
    if ($isLegacy) {
        Add-Node $package Launch @{ Condition = 'Installed OR VersionNT >= 601'; Message = 'This legacy client requires Windows 7 SP1 or newer and .NET Framework 4.8.' } | Out-Null
        Add-Node $package Launch @{ Condition = 'Installed OR VersionNT <> 601 OR ServicePackLevel >= 1'; Message = 'Install Windows 7 Service Pack 1 before installing this client.' } | Out-Null
        $framework = Add-Node $package Property @{ Id = 'NETFRAMEWORK_RELEASE' }
        Add-Node $framework RegistrySearch @{ Id = 'FrameworkRelease'; Root = 'HKLM'; Key = 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'; Name = 'Release'; Type = 'raw'; Bitness = 'always32' } | Out-Null
        # MSI raw REG_DWORD searches include a # prefix. Compare matching prefixed release values.
        Add-Node $package Launch @{ Condition = 'Installed OR (NETFRAMEWORK_RELEASE AND NETFRAMEWORK_RELEASE >= "#528040")'; Message = 'Install Microsoft .NET Framework 4.8 first, then run this installer again.' } | Out-Null
    } else { Add-Node $package Launch @{ Condition = 'VersionNT64'; Message = 'AutoBack requires 64-bit Windows.' } | Out-Null }
    if ($isAgent) {
        $property = Add-Node $package Property @{ Id = 'LEGACY_AUTOBACK_SERVICE' }
        Add-Node $property RegistrySearch @{ Id = 'ExistingAgentService'; Root = 'HKLM'; Key = 'SYSTEM\CurrentControlSet\Services\AutoBackAgent'; Name = 'ImagePath'; Type = 'raw' } | Out-Null
        Add-Node $package Launch @{ Condition = 'NOT LEGACY_AUTOBACK_SERVICE OR Installed OR WIX_UPGRADE_DETECTED'; Message = 'An existing AutoBackAgent service was found. For the first MSI install, unregister the legacy script-installed service first; retain its data. MSI upgrades use the existing product upgrade path.' } | Out-Null
    }
    $programFilesId = if ($isLegacy) { 'ProgramFilesFolder' } else { 'ProgramFiles64Folder' }
    $componentBitness = if ($isLegacy) { 'always32' } else { 'always64' }
    $standard = Add-Node $package StandardDirectory @{ Id = $programFilesId }
    $install = Add-Node $standard Directory @{ Id = 'INSTALLFOLDER'; Name = "AutoBack $displayRole" }
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
        $component = Add-Node $directories[$directory] Component @{ Id = "C$id"; Guid = $guid.ToString(); Bitness = $componentBitness }
        Add-Node $component File @{ Id = "F$id"; Source = $file.FullName; KeyPath = 'yes' } | Out-Null
        Add-Node $feature ComponentRef @{ Id = "C$id" } | Out-Null
        if ($isAgent -and $relative -eq $main) {
            $service = Add-Node $component ServiceInstall @{ Id = 'InstallAgent'; Name = 'AutoBackAgent'; DisplayName = 'AutoBack Agent'; Description = 'AutoBack backup, upload and retention worker'; Type = 'ownProcess'; Start = 'demand'; ErrorControl = 'normal'; Account = 'NT AUTHORITY\LocalService'; Arguments = 'run --machine' }
            Add-Node $service ServiceConfig @{ FirstFailureActionType = 'restart'; SecondFailureActionType = 'restart'; ThirdFailureActionType = 'none'; ResetPeriodInDays = '1'; RestartServiceDelayInSeconds = '60' } 'http://wixtoolset.org/schemas/v4/wxs/util' | Out-Null
            Add-Node $component ServiceControl @{ Id = 'ControlAgent'; Name = 'AutoBackAgent'; Stop = 'both'; Remove = 'uninstall'; Wait = 'yes' } | Out-Null
        }
    }
    $menu = Add-Node $package StandardDirectory @{ Id = 'ProgramMenuFolder' }
    $menuFolder = Add-Node $menu Directory @{ Id = 'MenuFolder'; Name = "AutoBack $displayRole" }
    $menuId = StableId "$role/menu"
    $shortcut = Add-Node $menuFolder Component @{ Id = 'MenuComponent'; Guid = ([Guid]::new(($menuId + '00').Insert(20, '-').Insert(16, '-').Insert(12, '-').Insert(8, '-'))).ToString(); Bitness = $componentBitness }
    if ($role -eq 'Host') {
        Add-Node $shortcut Shortcut @{ Id = 'LaunchHost'; Name = 'AutoBack Host'; Target = ('[#F' + (StableId "$role/$main") + ']'); WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
    } elseif ($isLegacy) {
        Add-Node $shortcut Shortcut @{ Id = 'SetupAgent'; Name = 'Configure AutoBack Agent'; Target = ('[#F' + (StableId "$role/$main") + ']'); Arguments = 'installed-setup'; WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
        Add-Node $shortcut Shortcut @{ Id = 'ChooseBackupFolders'; Name = 'Choose AutoBack backup folders'; Target = ('[#F' + (StableId "$role/$main") + ']'); Arguments = 'installed-setup --folders-only'; WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
    } else {
        Add-Node $shortcut Shortcut @{ Id = 'SetupAgent'; Name = 'Configure AutoBack Agent'; Target = '[System64Folder]WindowsPowerShell\v1.0\powershell.exe'; Arguments = '-NoProfile -File "[INSTALLFOLDER]scripts\Configure-InstalledAgent.ps1"'; WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
        Add-Node $shortcut Shortcut @{ Id = 'ChooseBackupFolders'; Name = 'Choose AutoBack backup folders'; Target = '[System64Folder]WindowsPowerShell\v1.0\powershell.exe'; Arguments = '-NoProfile -File "[INSTALLFOLDER]scripts\Configure-InstalledAgent.ps1" -FoldersOnly'; WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
    }
    $notepadTarget = if ($isLegacy) { '[SystemFolder]notepad.exe' } else { '[System64Folder]notepad.exe' }
    $guide = if ($isLegacy) { 'WINDOWS7-CLIENT.md' } else { 'AUTOMATION-ROLLOUT.md' }
    Add-Node $shortcut Shortcut @{ Id = 'ReadGuide'; Name = 'AutoBack rollout guide'; Target = $notepadTarget; Arguments = ('"[INSTALLFOLDER]docs\' + $guide + '"'); WorkingDirectory = 'INSTALLFOLDER' } | Out-Null
    Add-Node $shortcut RemoveFolder @{ Id = 'RemoveMenuFolder'; On = 'uninstall' } | Out-Null
    Add-Node $shortcut RegistryValue @{ Root = 'HKLM'; Key = "Software\AutoBack\$role"; Name = 'InstallDirectory'; Value = '[INSTALLFOLDER]'; Type = 'string'; KeyPath = 'yes' } | Out-Null
    Add-Node $feature ComponentRef @{ Id = 'MenuComponent' } | Out-Null
    $wxs = Join-Path $stageRoot "$role.wxs"; $xml.Save($wxs)
    $packageRole = if ($isLegacy) { 'Agent-Legacy' } else { $role }
    $msi = Join-Path $output "AutoBack-$packageRole-$Version-win-$architecture.msi"
    & $wix build $wxs -arch $architecture -ext WixToolset.Util.wixext -out $msi -pdbtype none
    if ($LASTEXITCODE -ne 0) { throw "$role MSI build failed." }
    Write-Output "Built $msi"
}
$checksums = @(Get-ChildItem -LiteralPath $output -Filter "AutoBack-*-$Version-win-*.msi" | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name })
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $checksums)
Write-Output 'MSIs built. No installation or service changes were performed. Verify them on pilot PCs using docs\ROLLOUT-CHECKLIST.md.'
