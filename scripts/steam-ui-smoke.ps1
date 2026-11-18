param(
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\ROUNDS',
    [ValidateRange(0,120)][int] $HoldSeconds = 15,
    [switch] $Visible,
    [switch] $SandboxFont
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $root '.runtime\steam-ui'
$output = Join-Path $root ('artifacts\steam-ui\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $runtime 'ROUNDS.exe'
foreach ($running in @(Get-Process ROUNDS -ErrorAction SilentlyContinue)) {
    if ($running.Path -eq $exe) { throw "Previous Steam UI test is still running: $($running.Id)" }
}
$fingerprintFiles = @('ROUNDS.exe','UnityPlayer.dll','ROUNDS_Data\globalgamemanagers','ROUNDS_Data\Managed\Assembly-CSharp.dll','ROUNDS_Data\Managed\PhotonUnityNetworking.dll')
if (-not (Test-Path -LiteralPath $exe)) {
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    Copy-Item -Path (Join-Path $GameDir '*') -Destination $runtime -Recurse -Force -Exclude 'output_log.txt'
}
foreach ($relative in $fingerprintFiles) {
    if ((Get-FileHash -LiteralPath (Join-Path $runtime $relative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $GameDir $relative)).Hash) {
        throw "Steam UI runtime is stale: $relative"
    }
}
Copy-Item -LiteralPath (Join-Path $root '.cache\BepInEx') -Destination $runtime -Recurse -Force
$plugin = Join-Path $runtime 'BepInEx\plugins\RoundsP2P'
$config = Join-Path $runtime 'BepInEx\config'
New-Item -ItemType Directory -Path (Join-Path $plugin 'luxon'), $config -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src\RoundsP2P.Plugin\bin\Release\netstandard2.1\RoundsP2P.dll'), (Join-Path $root 'src\RoundsP2P.Core\bin\Release\netstandard2.0\RoundsP2P.Core.dll') -Destination $plugin -Force
Copy-Item -LiteralPath (Join-Path $root '.cache\Luxon\v1.5\luxon_server.exe') -Destination (Join-Path $plugin 'luxon') -Force
"[Chainloader]`nHideManagerGameObject = true`n[Logging.Console]`nEnabled = false" | Set-Content -LiteralPath (Join-Path $config 'BepInEx.cfg') -Encoding utf8
'1557740' | Set-Content -LiteralPath (Join-Path $runtime 'steam_appid.txt') -Encoding ascii
$trace = Join-Path $output 'trace.log'
$unityLog = Join-Path $output 'unity.log'
$arguments = @('-screen-width','1100','-screen-height','700','-screen-fullscreen','0','-logFile',('"'+$unityLog+'"'), '--rounds-p2p-steam-ui-smoke', ('--rounds-p2p-steam-ui-hold='+$HoldSeconds), ('"--rounds-p2p-trace='+$trace+'"'))
if ($SandboxFont) {
    $arguments = @($arguments | Where-Object { $_ -ne '--rounds-p2p-steam-ui-smoke' }) + '--rounds-p2p-sandbox-font-smoke'
}
$windowStyle = 'Hidden'
if ($Visible) { $windowStyle='Normal' } else { $arguments += '-batchmode' }
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $runtime -WindowStyle $windowStyle -PassThru
Write-Output "Steam UI test PID $($process.Id), logs: $output"
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(90+$HoldSeconds)
    do {
        Start-Sleep -Milliseconds 500
        if ([DateTime]::UtcNow -gt $deadline) { throw "Steam UI test timed out. Read $unityLog" }
    } until ($process.HasExited)
    $text=if(Test-Path -LiteralPath $trace) { Get-Content -LiteralPath $trace -Raw } else { '' }
    if ($SandboxFont) {
        if ($process.ExitCode -ne 0 -or $text -notmatch "\tsandbox_font_smoke_complete\t") { throw "Sandbox font check failed. Read $trace" }
        Write-Output 'PASS: Sandbox rendered the correct Cyrillic, Latin and digit glyphs on both opens.'
        return
    }
    if($process.ExitCode -ne 0 -or $text -notmatch "\tsteam_ui_stock_online_verified\t" -or
        $text -notmatch "\tsteam_ui_smoke_complete\t" -or $text -match "\tsteam_ui_smoke_failed\t") {
        throw "Steam UI check failed. Read $trace and $unityLog"
    }
    Write-Output 'PASS: actual Steam lobby, host Photon room, invite availability and cleanup; no invitations sent.'
}
finally {
    if(-not $process.HasExited) { $process.Kill(); $null=$process.WaitForExit(5000) }
    $process.Dispose()
    if(Test-Path -LiteralPath $trace) {
        foreach($match in [regex]::Matches((Get-Content -LiteralPath $trace -Raw), "\tserver_started\tpid=(\d+)")) {
            $server=Get-Process -Id ([int]$match.Groups[1].Value) -ErrorAction SilentlyContinue
            $expected=Join-Path $plugin 'luxon\luxon_server.exe'
            if($server -and $server.Path -eq $expected) { Stop-Process -Id $server.Id -Force; $null=$server.WaitForExit(5000) }
        }
    }
}
