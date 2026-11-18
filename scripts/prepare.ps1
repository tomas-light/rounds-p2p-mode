param(
    [string] $LoaderProfile = (Join-Path $env:APPDATA 'r2modmanPlus-local\ROUNDS\profiles\Rounds P2P Prototype')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$coreSource = Join-Path $LoaderProfile 'BepInEx\core'
$coreTarget = Join-Path $root '.cache\BepInEx\core'
$sourceAvailable = Test-Path -LiteralPath (Join-Path $coreSource 'BepInEx.dll') -PathType Leaf
foreach ($requiredFile in @('BepInEx.dll', 'BepInEx.Preloader.dll', '0Harmony.dll')) {
    $referenceDirectory = if ($sourceAvailable) { $coreSource } else { $coreTarget }
    if (-not (Test-Path -LiteralPath (Join-Path $referenceDirectory $requiredFile) -PathType Leaf)) {
        throw "BepInEx 5 loader file missing: $referenceDirectory\$requiredFile. Pass -LoaderProfile with an existing r2modman profile containing BepInEx."
    }
}
New-Item -ItemType Directory -Path $coreTarget -Force | Out-Null
if ($sourceAvailable) { Get-ChildItem -LiteralPath $coreSource -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $coreTarget $_.Name) -Force
} }

$luxonDirectory = Join-Path $root '.cache\Luxon\v1.5'
New-Item -ItemType Directory -Path $luxonDirectory -Force | Out-Null
$downloads = @(
    @{
        Name = 'luxon_server.exe'
        Url = 'https://github.com/niansa/LuxonServer/releases/download/v1.5/luxon_server.msvc.release.exe'
        Sha256 = 'd5b272f49b21757d36094f30f986e4f4d2f1502eec79be33ce998ea19fff3286'
    },
    @{
        Name = 'LICENSE'
        Url = 'https://raw.githubusercontent.com/niansa/LuxonServer/e4bab98d8d6b3aee9ecf4035309a78a59b584f93/LICENSE'
        Sha256 = '87ca0810c9a251d379ca432fdcde1b8168e4f172a474b46a32be2386e022e766'
    }
)
foreach ($download in $downloads) {
    $target = Join-Path $luxonDirectory $download.Name
    if ((Test-Path -LiteralPath $target -PathType Leaf) -and
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $download.Sha256) { continue }
    $temporaryPath = Join-Path $luxonDirectory ($download.Name + '.' + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $download.Url -OutFile $temporaryPath
        $actual = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash
        if ($actual -ne $download.Sha256) { throw "SHA-256 mismatch for $($download.Name): $actual" }
        Move-Item -LiteralPath $temporaryPath -Destination $target -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}
Write-Output 'Prepared BepInEx loader references and verified Luxon Server v1.5. No game/mod profile was changed.'
