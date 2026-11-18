param(
    [switch] $SkipTests,
    [switch] $SteamTests,
    [string] $LoaderProfile = (Join-Path $env:APPDATA 'r2modmanPlus-local\ROUNDS\profiles\Rounds P2P Prototype')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'prepare.ps1') -LoaderProfile $LoaderProfile
Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') build 'src\RoundsP2P.Plugin\RoundsP2P.Plugin.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed with exit code $LASTEXITCODE." }
    if (-not $SkipTests) {
        foreach ($project in @('tests\RoundsP2P.Core.Tests\RoundsP2P.Core.Tests.csproj', 'tests\RoundsP2P.Network.Tests\RoundsP2P.Network.Tests.csproj')) {
            & (Join-Path $PSScriptRoot 'dotnet.ps1') test $project -c Release
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project (exit code $LASTEXITCODE)." }
        }
        if ($SteamTests) {
            $steamProject = 'tests\RoundsP2P.Steam.Tests\RoundsP2P.Steam.Tests.csproj'
            Write-Output 'Running real Steam SDK tests; these require a signed-in Steam account and normal process rights.'
            & (Join-Path $PSScriptRoot 'dotnet.ps1') test $steamProject -c Release
            if ($LASTEXITCODE -ne 0) { throw "Steam SDK tests failed (exit code $LASTEXITCODE)." }
        }
        else {
            Write-Output 'Steam SDK tests are separate: use -SteamTests with a signed-in Steam account and normal process rights. No Steam test result is implied by the default build.'
        }
    }
    elseif ($SteamTests) { Write-Output 'All tests were skipped by -SkipTests; requested Steam SDK tests were not run.' }
    $manifestPath = Join-Path $root 'thunderstore\manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.name -ne 'RoundsP2PPrototype' -or $manifest.version_number -ne '0.2.3') { throw 'Unexpected prototype package identity.' }
    $artifactDirectory = Join-Path $root 'artifacts'
    $staging = Join-Path $artifactDirectory ('package-' + [Guid]::NewGuid().ToString('N'))
    $pluginDirectory = Join-Path $staging 'BepInEx\plugins\RoundsP2P'
    $luxonDirectory = Join-Path $pluginDirectory 'luxon'
    New-Item -ItemType Directory -Path $luxonDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'src\RoundsP2P.Plugin\bin\Release\netstandard2.1\RoundsP2P.dll') -Destination $pluginDirectory
    Copy-Item -LiteralPath (Join-Path $root 'src\RoundsP2P.Core\bin\Release\netstandard2.0\RoundsP2P.Core.dll') -Destination $pluginDirectory
    Copy-Item -LiteralPath (Join-Path $root '.cache\Luxon\v1.5\luxon_server.exe'), (Join-Path $root '.cache\Luxon\v1.5\LICENSE') -Destination $luxonDirectory
    Copy-Item -LiteralPath $manifestPath -Destination $staging
    Copy-Item -LiteralPath (Join-Path $root 'thunderstore\icon.png') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $root 'thunderstore\README.md') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $staging
    $packagePath = Join-Path $artifactDirectory ($manifest.name + '-' + $manifest.version_number + '.zip')
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $packagePath -Force
    if (-not $SkipTests) {
        & (Join-Path $root 'tests\Packaging.Tests.ps1') -PackagePath $packagePath
    }
    Write-Output "Package: $packagePath"
    Write-Output ('SHA-256: ' + (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash)
}
finally { Pop-Location }
