param(
    [string] $ProfileName = 'Rounds P2P Prototype',
    [string] $LoaderSource = (Join-Path $env:APPDATA 'r2modmanPlus-local\ROUNDS\profiles\rounds p2p')
)
$ErrorActionPreference = 'Stop'
if ($ProfileName -notmatch '^[A-Za-z0-9 _-]+$') { throw 'Use a simple profile name without path separators.' }
$profilesRoot = Join-Path $env:APPDATA 'r2modmanPlus-local\ROUNDS\profiles'
$target = [IO.Path]::GetFullPath((Join-Path $profilesRoot $ProfileName))
if ((Split-Path -Parent $target) -ne [IO.Path]::GetFullPath($profilesRoot)) { throw 'Profile must stay inside the ROUNDS profile directory.' }
if (Test-Path -LiteralPath $target) { throw "Profile already exists; refusing to overwrite: $target" }
if (-not (Test-Path -LiteralPath (Join-Path $LoaderSource 'BepInEx\core\BepInEx.dll'))) { throw 'Loader source does not contain BepInEx 5.' }
New-Item -ItemType Directory -Path (Join-Path $target 'BepInEx\config'), (Join-Path $target 'BepInEx\plugins'), (Join-Path $target 'BepInEx\patchers') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $LoaderSource 'BepInEx\core') -Destination (Join-Path $target 'BepInEx') -Recurse
foreach ($name in @('winhttp.dll', 'doorstop_config.ini', 'corlibs')) {
    $source = Join-Path $LoaderSource $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $target -Recurse }
}
@'
[Chainloader]
HideManagerGameObject = true
[Logging.Console]
Enabled = false
'@ | Set-Content -LiteralPath (Join-Path $target 'BepInEx\config\BepInEx.cfg') -Encoding utf8
$sourceMods = Get-Content -LiteralPath (Join-Path $LoaderSource 'mods.yml') -Raw
$loaderRecord = [regex]::Match($sourceMods, '(?ms)^- manifestVersion: 1\r?\n  name: BepInEx-BepInExPack_ROUNDS\r?\n.*?(?=^- manifestVersion: 1|\z)').Value
if (-not $loaderRecord) { throw 'Cannot find BepInEx package record in source profile.' }
$loaderRecord | Set-Content -LiteralPath (Join-Path $target 'mods.yml') -Encoding utf8
Write-Output "Created clean BepInEx-only profile: $target"
