param(
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\ROUNDS',
    [int] $TimeoutSeconds = 330,
    [int] $MasterPort = 16055,
    [int] $GamePort = 16056,
    [switch] $Visible,
    [switch] $Mute,
    [switch] $FailureRecovery,
    [switch] $MatchHud
)
$ErrorActionPreference = 'Stop'
if ($FailureRecovery -and $MatchHud) { throw 'Choose one smoke scenario.' }
$root = Split-Path -Parent $PSScriptRoot
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path $root ('artifacts\game-smoke\' + $runId)
New-Item -ItemType Directory -Path $output -Force | Out-Null
# A new installed build gets new isolated copies; stale files are never reused.
$fingerprintFiles = @('ROUNDS.exe', 'UnityPlayer.dll', 'ROUNDS_Data\globalgamemanagers',
    'ROUNDS_Data\Managed\Assembly-CSharp.dll', 'ROUNDS_Data\Managed\PhotonUnityNetworking.dll',
    'ROUNDS_Data\Managed\PhotonRealtime.dll', 'ROUNDS_Data\Managed\Photon3Unity3D.dll')
$fingerprints = foreach ($relative in $fingerprintFiles) {
    [pscustomobject]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath (Join-Path $GameDir $relative) -Algorithm SHA256).Hash }
}
$fingerprintText = ($fingerprints | ConvertTo-Json -Compress)
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
try { $copyId = [BitConverter]::ToString($hashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprintText))).Replace('-', '').Substring(0, 16) }
finally { $hashAlgorithm.Dispose() }
$copies = Join-Path $root ('.runtime\game-smoke\' + $copyId)
$fingerprints | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'game-fingerprints.json') -Encoding utf8
$pluginDll = Join-Path $root 'src\RoundsP2P.Plugin\bin\Release\netstandard2.1\RoundsP2P.dll'
$coreDll = Join-Path $root 'src\RoundsP2P.Core\bin\Release\netstandard2.0\RoundsP2P.Core.dll'
$server = Join-Path $root '.cache\Luxon\v1.5\luxon_server.exe'
foreach ($file in @($pluginDll, $coreDll, $server, (Join-Path $GameDir 'ROUNDS.exe'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing prerequisite: $file. Run scripts/build.ps1 first." }
}
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Start Steam and sign in normally before the game smoke test.' }
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$serverIds = [Collections.Generic.List[int]]::new()
try {
    foreach ($role in @('host', 'client')) {
        $directory = Join-Path $copies $role
        $executable = Join-Path $directory 'ROUNDS.exe'
        foreach ($running in @(Get-Process ROUNDS -ErrorAction SilentlyContinue)) {
            if ($running.Path -eq $executable) { throw "Previous smoke process still running: $($running.Id). Stop that exact process before retrying." }
        }
        if (-not (Test-Path -LiteralPath $executable)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            Copy-Item -Path (Join-Path $GameDir '*') -Destination $directory -Recurse -Force -Exclude 'output_log.txt'
        }
        foreach ($fingerprint in $fingerprints) {
            if ((Get-FileHash -LiteralPath (Join-Path $directory $fingerprint.path) -Algorithm SHA256).Hash -ne $fingerprint.sha256) {
                throw "Cached game copy differs from installed game: $($fingerprint.path)"
            }
        }
        Copy-Item -LiteralPath (Join-Path $root '.cache\BepInEx') -Destination $directory -Recurse -Force
        $config = Join-Path $directory 'BepInEx\config'
        $plugin = Join-Path $directory 'BepInEx\plugins\RoundsP2P'
        New-Item -ItemType Directory -Path $config, (Join-Path $plugin 'luxon') -Force | Out-Null
        "[Chainloader]`nHideManagerGameObject = true`n[Logging.Console]`nEnabled = false" | Set-Content -LiteralPath (Join-Path $config 'BepInEx.cfg') -Encoding utf8
        Copy-Item -LiteralPath $pluginDll, $coreDll -Destination $plugin -Force
        Copy-Item -LiteralPath $server -Destination (Join-Path $plugin 'luxon\luxon_server.exe') -Force
        '1557740' | Set-Content -LiteralPath (Join-Path $directory 'steam_appid.txt') -Encoding ascii
        $trace = Join-Path $output ($role + '-trace.log')
        $unityLog = Join-Path $output ($role + '-unity.log')
        $roleArgument = if ($role -eq 'host') { 'Host' } else { 'Client' }
        $arguments = @('-screen-width', '800', '-screen-height', '450', '-screen-fullscreen', '0',
            '-logFile', ('"' + $unityLog + '"'), ('--rounds-p2p-role=' + $roleArgument), '--rounds-p2p-smoke',
            ('"--rounds-p2p-trace=' + $trace + '"'), ('--rounds-p2p-master-port=' + $MasterPort), ('--rounds-p2p-game-port=' + $GamePort))
        if ($role -eq 'host') { $arguments += '--rounds-p2p-cancel-retry-smoke' }
        if ($role -eq 'host' -and $FailureRecovery) { $arguments += '--rounds-p2p-steam-failure-smoke' }
        if ($MatchHud) { $arguments += '--rounds-p2p-hud-smoke' }
        if ($Mute) { $arguments += '--rounds-p2p-mute' }
        $windowStyle = 'Hidden'
        if ($Visible) { $windowStyle = 'Normal' } else { $arguments += '-batchmode' }
        $process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $directory -WindowStyle $windowStyle -PassThru
        $processes.Add($process)
        Write-Output "Started $role PID $($process.Id)"
        if ($role -eq 'host') {
            $startupDeadline = [DateTime]::UtcNow.AddSeconds(45)
            do {
                Start-Sleep -Milliseconds 300
                $traceText = if (Test-Path -LiteralPath $trace) { Get-Content -LiteralPath $trace -Raw } else { '' }
                if ($process.HasExited) { throw "Host exited before joining its room. Read $unityLog" }
                foreach ($serverMatch in [regex]::Matches($traceText, "\tserver_started\tpid=(\d+)")) {
                    $ownedServer = [int]$serverMatch.Groups[1].Value
                    if (-not $serverIds.Contains($ownedServer)) { $serverIds.Add($ownedServer) }
                }
                if ($traceText -match "\terror\t") { throw "Host failed. Read $trace" }
                if ([DateTime]::UtcNow -gt $startupDeadline) { throw "Host startup timed out. Read $trace" }
            } until ($traceText -match "\troom_joined\t")
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastReport = [DateTime]::UtcNow
    do {
        Start-Sleep -Milliseconds 500
        $complete = $true
        if ($FailureRecovery) {
            $hostTrace = Join-Path $output 'host-trace.log'
            $hostText = if (Test-Path -LiteralPath $hostTrace) { Get-Content -LiteralPath $hostTrace -Raw } else { '' }
            if ($hostText -match "\tsmoke_failed\t") { throw "Failure recovery check failed. Read $hostTrace" }
            $complete = $hostText -match "\tsmoke_failure_recovery_complete\tgame_stopped;menu_restored;photon_disconnected"
        }
        else {
        foreach ($role in @('host', 'client')) {
            $trace = Join-Path $output ($role + '-trace.log')
            $traceText = if (Test-Path -LiteralPath $trace) { Get-Content -LiteralPath $trace -Raw } else { '' }
            if ($traceText -match "\t(smoke_failed|error)\t") { throw "Game smoke failed. Read $trace" }
            if ($traceText -notmatch "\tsmoke_capacity_verified\tmax_players=2") { $complete = $false }
            if ($traceText -notmatch "\tsmoke_match_complete\twinner=[01];rounds=(5:[0-4]|[0-4]:5);limit=5") { $complete = $false }
            if ($MatchHud -and ($traceText -notmatch "\tsmoke_initial_hud_verified\t" -or
                $traceText -notmatch "\trematch_preview_skipped\t" -or $traceText -notmatch "\trematch_map_ready\t" -or
                $traceText -notmatch "\trematch_battle_ready\t" -or
                $traceText -notmatch "\tsmoke_rematch_hud_verified\torange=1;blue=0;rounds=0:0" -or
                ($role -eq 'client' -and $traceText -notmatch "\tsmoke_delayed_rematch_map\t"))) { $complete = $false }
        }
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Full match timed out. Read $output" }
        if (([DateTime]::UtcNow - $lastReport).TotalSeconds -gt 20) {
            $lastReport = [DateTime]::UtcNow
            foreach ($role in @('host', 'client')) {
                $trace = Join-Path $output ($role + '-trace.log')
                if (Test-Path -LiteralPath $trace) { Get-Content -LiteralPath $trace -Tail 1 | ForEach-Object { Write-Output "$role $_" } }
            }
        }
    } until ($complete)
    if ($FailureRecovery) {
        [pscustomobject]@{ scenario = 'Steam failure during an active battle'; result = 'game_stopped;menu_restored;photon_disconnected'; trace = (Join-Path $output 'host-trace.log') } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
        Write-Output "PASS: Steam controller failure stopped an active battle and restored the menu. Evidence: $output"
        return
    }
    $results = foreach ($role in @('host', 'client')) {
        $trace = Join-Path $output ($role + '-trace.log')
        $events = Get-Content -LiteralPath $trace
        if ($MatchHud) {
            $matchEnd = ($events | Select-String -Pattern '\tsmoke_match_complete\t' | Select-Object -Last 1).LineNumber
            $rematchEvents = @($events |
                Select-String -Pattern '\t(rematch_preview_skipped|smoke_rematch_prompt_closed|smoke_delayed_rematch_map|rematch_map_ready|smoke_card_picked|rematch_battle_ready|smoke_rematch_hud_verified)\t' |
                Where-Object { $_.LineNumber -gt $matchEnd } |
                ForEach-Object { ($_.Line -split "`t", 3)[1] })
            $expected = @('rematch_preview_skipped', 'smoke_rematch_prompt_closed')
            if ($role -eq 'client') { $expected += 'smoke_delayed_rematch_map' }
            $expected += @('rematch_map_ready', 'smoke_card_picked', 'rematch_battle_ready', 'smoke_rematch_hud_verified')
            if (($rematchEvents -join ',') -ne ($expected -join ',')) {
                throw "Unexpected rematch event order for $role. Read $trace"
            }
        }
        $terminal = ($events | Where-Object { $_ -match "\tsmoke_match_complete\t" } | Select-Object -Last 1) -split "`t", 3
        [pscustomobject]@{ role = $role; result = $terminal[2]; cardsPicked = @($events | Where-Object { $_ -match "\tsmoke_card_picked\t" }).Count; trace = $trace }
    }
    if ($results[0].result -ne $results[1].result) { throw 'Clients disagree about final match result.' }
    if ((Get-Content -LiteralPath (Join-Path $output 'host-trace.log') -Raw) -notmatch "\tsmoke_cancel_retry_complete\t") { throw 'Cancellation/retry regression was not exercised.' }
    if (@($results | Where-Object cardsPicked -lt 1).Count -gt 0) { throw 'Both clients must have exercised normal card selection.' }
    $results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
    foreach ($process in $processes) { $null = $process.WaitForExit(10000) }
    if ($MatchHud) { Write-Output "PASS: prompt closed after confirmation, one map load per client, delayed guest, both loaded before picks, and a stable battle with lobby score 1:0. Evidence: $output" }
    else { Write-Output "PASS: both real game clients completed the normal five-round match. Evidence: $output" }
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
    # Only server PIDs reported by this run; verify the exact executable before cleanup.
    foreach ($serverId in $serverIds) {
        $owned = Get-Process -Id $serverId -ErrorAction SilentlyContinue
        $expected = Join-Path $copies 'host\BepInEx\plugins\RoundsP2P\luxon\luxon_server.exe'
        if ($owned -and $owned.Path -eq $expected) { Stop-Process -Id $serverId -Force; $null = $owned.WaitForExit(5000) }
    }
}
