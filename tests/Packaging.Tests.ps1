param([string] $PackagePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $PackagePath) { $PackagePath = Join-Path $root 'artifacts\RoundsP2PPrototype-0.2.3.zip' }
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'Build the prototype ZIP before running packaging tests.' }
$installer = Join-Path $root 'scripts\install-profile.ps1'
$fixture = Join-Path $root ('.cache\packaging-tests\' + [Guid]::NewGuid().ToString('N'))
$fakeAppData = Join-Path $fixture 'AppData'
$profile = Join-Path $fakeAppData 'r2modmanPlus-local\ROUNDS\profiles\Rounds P2P Prototype'
$coreDirectory = Join-Path $profile 'BepInEx\core'
$pluginsDirectory = Join-Path $profile 'BepInEx\plugins'
$modsPath = Join-Path $profile 'mods.yml'
$cacheRoot = Join-Path $fakeAppData 'r2modmanPlus-local\ROUNDS\cache'
$packageCache = Join-Path $cacheRoot 'Local-RoundsP2PPrototype\0.2.3'
$unrelatedCache = Join-Path $cacheRoot 'UnrelatedPackage\1.0.0\sentinel.txt'
New-Item -ItemType Directory -Path $coreDirectory, $pluginsDirectory, (Join-Path $profile 'BepInEx\patchers') -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $unrelatedCache) -Force | Out-Null
Set-Content -LiteralPath $unrelatedCache -Value 'unrelated-cache-preserved' -Encoding ascii
$unrelatedHash = (Get-FileHash -LiteralPath $unrelatedCache -Algorithm SHA256).Hash
$cachedLoader = Join-Path $root '.cache\BepInEx\core\BepInEx.dll'
if (Test-Path -LiteralPath $cachedLoader -PathType Leaf) {
    Copy-Item -LiteralPath $cachedLoader -Destination $coreDirectory
}
else {
    # The installer checks loader presence; these tests never execute the loader.
    Set-Content -LiteralPath (Join-Path $coreDirectory 'BepInEx.dll') -Value 'packaging-test-loader-placeholder' -Encoding ascii
}
@'
- manifestVersion: 1
  name: BepInEx-BepInExPack_ROUNDS
  authorName: BepInEx
  displayName: BepInExPack_ROUNDS
  dependencies: []
  versionNumber:
    major: 5
    minor: 4
    patch: 1901
  enabled: true
  onlineSource: false
'@ | Set-Content -LiteralPath $modsPath -Encoding utf8

function Get-ProfileSnapshot {
    $entries = foreach ($scope in @(@('profile', $profile), @('cache', $cacheRoot))) {
        foreach ($item in Get-ChildItem -LiteralPath $scope[1] -Recurse -Force | Sort-Object FullName) {
            $relative = $scope[0] + '|' + $item.FullName.Substring($scope[1].Length)
            if ($item.PSIsContainer) { 'D|' + $relative }
            else { 'F|' + $relative + '|' + (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
        }
    }
    return ($entries -join "`n")
}

function Assert-InstallerRejects {
    param([string] $ArchivePath, [string] $ExpectedMessage)
    $before = Get-ProfileSnapshot
    $rejected = $false
    try { & $installer -PackagePath $ArchivePath }
    catch {
        if ($_.Exception.Message -notlike $ExpectedMessage) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Installer accepted an invalid input: $ExpectedMessage" }
    if ((Get-ProfileSnapshot) -cne $before) { throw 'Rejected install mutated the fixture profile or package cache.' }
}

$savedAppData = $env:APPDATA
try {
    # All installer writes are redirected to this workspace-owned fixture.
    $env:APPDATA = $fakeAppData
    & $installer -PackagePath $PackagePath
    # Simulate the existing 0.1.0 profile before upgrading to the Steam build.
    $previousMods = Get-Content -LiteralPath $modsPath -Raw
    $previousRecord = [regex]::Match($previousMods, '(?ms)^- manifestVersion: 1\r?\n  name: Local-RoundsP2PPrototype\r?\n.*?(?=^- manifestVersion: 1|\z)').Value
    if (-not $previousRecord) { throw 'Initial install did not write a local package record.' }
    $previousMods.Replace($previousRecord, $previousRecord.Replace('    patch: 3', '    patch: 2')) |
        Set-Content -LiteralPath $modsPath -Encoding utf8
    & $installer -PackagePath $PackagePath
    & $installer -PackagePath $PackagePath
    $mods = Get-Content -LiteralPath $modsPath -Raw
    if ([regex]::Matches($mods, '(?m)^  name: Local-RoundsP2PPrototype\r?$').Count -ne 1) { throw 'Reinstall duplicated the local package record.' }
    if ([regex]::Matches($mods, '(?m)^  name: BepInEx-BepInExPack_ROUNDS\r?$').Count -ne 1) { throw 'Reinstall changed the loader record count.' }
    $ownRecord = [regex]::Match($mods, '(?ms)^- manifestVersion: 1\r?\n  name: Local-RoundsP2PPrototype\r?\n.*?(?=^- manifestVersion: 1|\z)').Value
    if ($ownRecord -notmatch '(?m)^    major: 0\r?$' -or $ownRecord -notmatch '(?m)^    minor: 2\r?$' -or $ownRecord -notmatch '(?m)^    patch: 3\r?$') {
        throw 'Installed local package metadata does not contain version 0.2.3.'
    }
    $dlls = @(Get-ChildItem -LiteralPath $pluginsDirectory -Filter '*.dll' -File -Recurse)
    if ($dlls.Count -ne 2 -or @($dlls.Name | Where-Object { $_ -notin @('RoundsP2P.dll', 'RoundsP2P.Core.dll') }).Count -ne 0) {
        throw 'Installed package does not contain exactly the two prototype plugin DLLs.'
    }
    $cachedIcon = Join-Path $packageCache 'icon.png'
    if (-not (Test-Path -LiteralPath $cachedIcon -PathType Leaf)) { throw 'Package icon was not installed in the r2modman cache.' }
    Add-Type -AssemblyName System.Drawing
    $image = [Drawing.Image]::FromFile($cachedIcon)
    try {
        if ($image.Width -ne 256 -or $image.Height -ne 256 -or $image.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid) {
            throw 'Cached icon is not a valid 256x256 PNG.'
        }
    }
    finally { $image.Dispose() }
    foreach ($relative in @('manifest.json', 'README.md', 'THIRD-PARTY-NOTICES.md', 'BepInEx\plugins\RoundsP2P\RoundsP2P.dll',
        'BepInEx\plugins\RoundsP2P\RoundsP2P.Core.dll', 'BepInEx\plugins\RoundsP2P\luxon\luxon_server.exe', 'BepInEx\plugins\RoundsP2P\luxon\LICENSE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageCache $relative) -PathType Leaf)) { throw "Incomplete package cache: $relative" }
    }
    if ((Get-FileHash -LiteralPath $unrelatedCache -Algorithm SHA256).Hash -ne $unrelatedHash) { throw 'Installer changed an unrelated cached package.' }
    if ($mods -match 'data:image/|base64,') { throw 'Package metadata must not embed the icon as base64.' }
    Write-Output 'PASS: initial install, 0.2.2 metadata upgrade and idempotent reinstall preserve version 0.2.3, one local record and exactly two plugin DLLs.'
    Write-Output 'PASS: a valid 256x256 PNG and full package are cached for r2modman; unrelated cached packages are preserved.'

    $otherDirectory = Join-Path $pluginsDirectory 'Other'
    New-Item -ItemType Directory -Path $otherDirectory -Force | Out-Null
    $otherDll = Join-Path $otherDirectory 'Other.dll'
    Set-Content -LiteralPath $otherDll -Value 'foreign-plugin-fixture' -Encoding ascii
    Assert-InstallerRejects -ArchivePath $PackagePath -ExpectedMessage 'Unexpected file in dedicated plugin profile:*'
    Write-Output 'PASS: a foreign plugin is rejected without any profile or cache mutation.'
    Remove-Item -LiteralPath $otherDll

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unsafeArchive = Join-Path $fixture 'unsafe.zip'
    Copy-Item -LiteralPath $PackagePath -Destination $unsafeArchive
    $zip = [IO.Compression.ZipFile]::Open($unsafeArchive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.CreateEntry('../escape.txt')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('unsafe-entry-fixture') }
        finally { $writer.Dispose() }
    }
    finally { $zip.Dispose() }
    Assert-InstallerRejects -ArchivePath $unsafeArchive -ExpectedMessage 'Unexpected or duplicate package entry:*'
    if (Test-Path -LiteralPath (Join-Path $fixture 'escape.txt')) { throw 'Unsafe archive entry was extracted.' }
    Write-Output 'PASS: ZIP traversal is rejected without any profile or cache mutation.'

    $invalidIconArchive = Join-Path $fixture 'invalid-icon.zip'
    Copy-Item -LiteralPath $PackagePath -Destination $invalidIconArchive
    $zip = [IO.Compression.ZipFile]::Open($invalidIconArchive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.GetEntry('icon.png')
        if ($null -eq $entry) { throw 'Test package does not contain the required icon.' }
        $entry.Delete()
        $entry = $zip.CreateEntry('icon.png')
        $stream = $entry.Open()
        try { $stream.Write([byte[]]@(0, 1, 2, 3), 0, 4) }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
    Assert-InstallerRejects -ArchivePath $invalidIconArchive -ExpectedMessage 'Invalid package icon:*'
    Write-Output 'PASS: an invalid icon is rejected before any profile or cache mutation.'

    $missingIconArchive = Join-Path $fixture 'missing-icon.zip'
    Copy-Item -LiteralPath $PackagePath -Destination $missingIconArchive
    $zip = [IO.Compression.ZipFile]::Open($missingIconArchive, [IO.Compression.ZipArchiveMode]::Update)
    try { $zip.GetEntry('icon.png').Delete() }
    finally { $zip.Dispose() }
    Assert-InstallerRejects -ArchivePath $missingIconArchive -ExpectedMessage 'Missing package entry: icon.png'
    Write-Output 'PASS: a missing required icon is rejected without any profile or cache mutation.'

    $wrongSizeArchive = Join-Path $fixture 'wrong-size-icon.zip'
    Copy-Item -LiteralPath $PackagePath -Destination $wrongSizeArchive
    $zip = [IO.Compression.ZipFile]::Open($wrongSizeArchive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.GetEntry('icon.png')
        $memory = [IO.MemoryStream]::new()
        $input = $entry.Open()
        try { $input.CopyTo($memory); $wrongSizeBytes = $memory.ToArray() }
        finally { $input.Dispose(); $memory.Dispose() }
        # Keep a PNG header but change its IHDR width from 256 to 512.
        $wrongSizeBytes[18] = 2
        $entry.Delete()
        $entry = $zip.CreateEntry('icon.png')
        $output = $entry.Open()
        try { $output.Write($wrongSizeBytes, 0, $wrongSizeBytes.Length) }
        finally { $output.Dispose() }
    }
    finally { $zip.Dispose() }
    Assert-InstallerRejects -ArchivePath $wrongSizeArchive -ExpectedMessage 'Invalid package icon:*'
    Write-Output 'PASS: a non-256-square PNG header is rejected without any profile or cache mutation.'
}
finally {
    if ($null -eq $savedAppData) { Remove-Item Env:APPDATA -ErrorAction SilentlyContinue }
    else { $env:APPDATA = $savedAppData }
}
Write-Output "Packaging tests passed. Fixture retained at: $fixture"
