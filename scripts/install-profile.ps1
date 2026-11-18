param(
    [ValidateSet('Rounds P2P Prototype')][string] $ProfileName = 'Rounds P2P Prototype',
    [string] $PackagePath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $PackagePath) { $PackagePath = Join-Path $root 'artifacts\RoundsP2PPrototype-0.2.3.zip' }
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$profilesRoot = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'r2modmanPlus-local\ROUNDS\profiles'))
$target = [IO.Path]::GetFullPath((Join-Path $profilesRoot $ProfileName))
if ((Split-Path -Parent $target) -ne $profilesRoot) { throw 'Profile must remain inside the ROUNDS profile directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $target 'BepInEx\core\BepInEx.dll') -PathType Leaf)) {
    throw 'Create the dedicated BepInEx-only prototype profile before installing the plugin.'
}
$modsPath = Join-Path $target 'mods.yml'
$mods = Get-Content -LiteralPath $modsPath -Raw
$records = [regex]::Matches($mods, '(?ms)^- manifestVersion: 1\r?\n.*?(?=^- manifestVersion: 1|\z)')
if ($records.Count -eq 0) { throw 'Profile mods.yml does not contain the expected package records.' }
$retainedRecords = @()
foreach ($record in $records) {
    $name = [regex]::Match($record.Value, '(?m)^  name: (.+)\r?$').Groups[1].Value.Trim()
    if ($name -notin @('BepInEx-BepInExPack_ROUNDS', 'Local-RoundsP2PPrototype')) { throw "Unexpected mod in dedicated profile: $name" }
    if ($name -eq 'BepInEx-BepInExPack_ROUNDS') { $retainedRecords += $record.Value.TrimEnd() }
}
if ($retainedRecords.Count -ne 1) { throw 'Expected exactly one BepInEx loader record.' }
$pluginsRoot = Join-Path $target 'BepInEx\plugins'
$ownRoot = [IO.Path]::GetFullPath((Join-Path $pluginsRoot 'RoundsP2P')) + [IO.Path]::DirectorySeparatorChar
if (Test-Path -LiteralPath $pluginsRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $pluginsRoot -File -Recurse) {
        if (-not $file.FullName.StartsWith($ownRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected file in dedicated plugin profile: $($file.FullName)" }
        if ($file.Extension -eq '.dll' -and $file.Name -notin @('RoundsP2P.dll', 'RoundsP2P.Core.dll')) { throw "Unexpected plugin DLL: $($file.Name)" }
    }
}
$patchers = Join-Path $target 'BepInEx\patchers'
if ((Test-Path -LiteralPath $patchers) -and (Get-ChildItem -LiteralPath $patchers -File -Recurse | Select-Object -First 1)) {
    throw 'The dedicated prototype profile must not contain preloader patchers.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
$allowedEntries = @('manifest.json', 'icon.png', 'README.md', 'THIRD-PARTY-NOTICES.md',
    'BepInEx/plugins/RoundsP2P/RoundsP2P.dll', 'BepInEx/plugins/RoundsP2P/RoundsP2P.Core.dll',
    'BepInEx/plugins/RoundsP2P/luxon/luxon_server.exe', 'BepInEx/plugins/RoundsP2P/luxon/LICENSE')
$foundEntries = @{}
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.EndsWith('/')) { continue }
        if ($name -notin $allowedEntries -or $foundEntries.ContainsKey($name)) { throw "Unexpected or duplicate package entry: $name" }
        $foundEntries[$name] = $entry
    }
    foreach ($name in $allowedEntries) { if (-not $foundEntries.ContainsKey($name)) { throw "Missing package entry: $name" } }
    $manifestReader = [IO.StreamReader]::new($foundEntries['manifest.json'].Open())
    try { $manifest = $manifestReader.ReadToEnd() | ConvertFrom-Json } finally { $manifestReader.Dispose() }
    if ($manifest.name -ne 'RoundsP2PPrototype' -or $manifest.version_number -ne '0.2.3' -or
        @($manifest.dependencies).Count -ne 1 -or $manifest.dependencies[0] -ne 'BepInEx-BepInExPack_ROUNDS-5.4.1901') { throw 'Unexpected package manifest.' }
    # Stage and validate the full package before changing the r2modman profile.
    $staging = Join-Path $root ('.cache\install-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    foreach ($name in $allowedEntries) {
        $destination = Join-Path $staging $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($foundEntries[$name], $destination)
    }
}
finally { $archive.Dispose() }
$serverPath = Join-Path $staging 'BepInEx\plugins\RoundsP2P\luxon\luxon_server.exe'
if ((Get-FileHash -LiteralPath $serverPath -Algorithm SHA256).Hash -ne 'd5b272f49b21757d36094f30f986e4f4d2f1502eec79be33ce998ea19fff3286') { throw 'Invalid Luxon binary in package.' }
$licensePath = Join-Path $staging 'BepInEx\plugins\RoundsP2P\luxon\LICENSE'
if ((Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash -ne '87ca0810c9a251d379ca432fdcde1b8168e4f172a474b46a32be2386e022e766') { throw 'Invalid Luxon license in package.' }
$iconBytes = [IO.File]::ReadAllBytes((Join-Path $staging 'icon.png'))
$pngHeader = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 1, 0, 0, 0, 1, 0)
if ($iconBytes.Length -lt 33) { throw 'Invalid package icon: expected a 256x256 PNG.' }
for ($index = 0; $index -lt $pngHeader.Length; $index++) {
    if ($iconBytes[$index] -ne $pngHeader[$index]) { throw 'Invalid package icon: expected a 256x256 PNG.' }
}

# r2modman resolves local package icons and disable/enable sources from this cache.
# All names are fixed, and unrelated packages/versions are left untouched.
$gameRoot = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'r2modmanPlus-local\ROUNDS'))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot 'cache'))
$ownCacheRoot = [IO.Path]::GetFullPath((Join-Path $cacheRoot 'Local-RoundsP2PPrototype'))
$packageCache = [IO.Path]::GetFullPath((Join-Path $ownCacheRoot '0.2.3'))
if ((Split-Path -Parent $cacheRoot) -ne $gameRoot -or (Split-Path -Parent $ownCacheRoot) -ne $cacheRoot -or
    (Split-Path -Parent $packageCache) -ne $ownCacheRoot) { throw 'Package cache must remain inside the fixed ROUNDS cache directory.' }
foreach ($path in @($ownCacheRoot, $packageCache)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The dedicated package cache must not be a symbolic link or junction.'
    }
}
foreach ($name in $allowedEntries) {
    $cacheDestination = Join-Path $packageCache $name
    New-Item -ItemType Directory -Path (Split-Path -Parent $cacheDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $staging $name) -Destination $cacheDestination -Force
}
$pluginDestination = Join-Path $pluginsRoot 'RoundsP2P'
New-Item -ItemType Directory -Path (Join-Path $pluginDestination 'luxon') -Force | Out-Null
foreach ($relative in @('RoundsP2P.dll', 'RoundsP2P.Core.dll', 'luxon\luxon_server.exe', 'luxon\LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $staging ('BepInEx\plugins\RoundsP2P\' + $relative)) -Destination (Join-Path $pluginDestination $relative) -Force
}
$installedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$localRecord = @"
- manifestVersion: 1
  name: Local-RoundsP2PPrototype
  authorName: Local
  websiteUrl: ''
  displayName: ROUNDS P2P Prototype
  description: Standalone Steam lobby and network prototype with bundled Luxon Server.
  gameVersion: '0'
  networkMode: both
  packageType: other
  installMode: managed
  installedAtTime: $installedAt
  loaders: []
  dependencies:
    - BepInEx-BepInExPack_ROUNDS-5.4.1901
  incompatibilities: []
  optionalDependencies: []
  versionNumber:
    major: 0
    minor: 2
    patch: 3
  enabled: true
  onlineSource: false
"@
($retainedRecords[0] + "`n" + $localRecord.TrimEnd() + "`n") | Set-Content -LiteralPath $modsPath -Encoding utf8
Write-Output "Installed ROUNDS P2P Prototype 0.2.3 into: $target"
Write-Output 'Launch this profile in r2modman, then choose Online -> Create lobby -> Invite friend.'
