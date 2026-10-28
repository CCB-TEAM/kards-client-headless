$ErrorActionPreference='Stop'
$previous=$env:HEADLESS_CARD_PACK
$serverProcess=$null
$bots=@()
$serverDirectory=Join-Path $PSScriptRoot 'artifacts\fyserver-isolated'
$runDirectory=Join-Path $PSScriptRoot ('artifacts\aggro-server-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
try {
    if(Get-NetTCPConnection -LocalPort 19145 -State Listen -ErrorAction SilentlyContinue){throw 'Port 19145 is occupied; leave the existing server alone'}
    $serverExe=Join-Path $serverDirectory 'fyserver.exe'
    if(-not (Test-Path -LiteralPath $serverExe)){throw 'Isolated fyserver fixture not found'}
    $settings=Get-Content -LiteralPath (Join-Path $serverDirectory 'setting.json') -Raw | ConvertFrom-Json
    if($settings.portHttp -ne 19145 -or $settings.listenIp -ne '127.0.0.1'){throw 'Unexpected isolated server settings'}
    $serverProcess=Start-Process -FilePath $serverExe -WorkingDirectory $serverDirectory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runDirectory 'server.stdout.log') -RedirectStandardError (Join-Path $runDirectory 'server.stderr.log')
    $ready=$false
    for($i=0;$i -lt 60;$i++){
        try {$null=Invoke-WebRequest 'http://127.0.0.1:19145/' -TimeoutSec 1;$ready=$true;break}catch{
            if(Get-NetTCPConnection -LocalPort 19145 -State Listen -ErrorAction SilentlyContinue){$ready=$true;break}
            if($serverProcess.HasExited){throw 'Isolated server exited during startup'}
            Start-Sleep -Milliseconds 500
        }
    }
    if(-not $ready){throw 'Isolated server did not start'}
    $env:HEADLESS_CARD_PACK=Join-Path $PSScriptRoot '..\CardExamples\pack-aggro.json'
    $tag=[guid]::NewGuid().ToString('N').Substring(0,8)
    foreach($name in @('left','right')){
        $config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bot-aggro.json') -Raw | ConvertFrom-Json
        $config.server='http://127.0.0.1:19145'
        $config.username='aggro_'+$tag+'_'+$name
        $config.battle_code='AGGRO'+$tag
        $config.poll_milliseconds=100
        $config.action_milliseconds=0
        $config.timeout_seconds=120
        $config.output_directory=Join-Path $runDirectory $name
        $configPath=Join-Path $runDirectory ($name+'.json')
        $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
        $bots+=Start-Process -FilePath 'dotnet' -ArgumentList @((Join-Path $PSScriptRoot 'out\HeadlessKards.dll'),'bot',$configPath) `
            -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $runDirectory ($name+'.stdout.log')) -RedirectStandardError (Join-Path $runDirectory ($name+'.stderr.log'))
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(135)
    while(($bots | Where-Object {-not $_.HasExited}).Count -gt 0){
        if([DateTime]::UtcNow -gt $deadline){throw 'Aggro server test timed out'}
        Start-Sleep -Milliseconds 500
    }
    foreach($bot in $bots){$bot.Refresh();if($bot.ExitCode -ne 0){throw "Bot failed; inspect $runDirectory"}}
    $checkpoints=@(Get-ChildItem -LiteralPath $runDirectory -Filter checkpoint.json -Recurse | ForEach-Object {Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json})
    if($checkpoints.Count -ne 2){throw 'Missing peer checkpoints'}
    if(-not $checkpoints[0].state.outcome -or $checkpoints[0].state_hash -ne $checkpoints[1].state_hash){throw 'Peer outcome/hash mismatch'}
    foreach($file in Get-ChildItem -LiteralPath $runDirectory -Filter wire-replay.json -Recurse){
        dotnet (Join-Path $PSScriptRoot 'out\HeadlessKards.dll') wire-replay $file.FullName
        if($LASTEXITCODE -ne 0){throw 'Live wire replay verification failed'}
    }
    $report=[ordered]@{server='http://127.0.0.1:19145';peers=2;deck_cards=39;outcome=$checkpoints[0].state.outcome;
        last_action=$checkpoints[0].last_action_id;state_hash=$checkpoints[0].state_hash;peer_hash_equal=$true;
        replay_verified=$true;native_client_tested=$false;artifacts=$runDirectory}
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\aggro-server-report.json') -Encoding utf8
    $report | ConvertTo-Json -Compress
} finally {
    foreach($bot in $bots){if(-not $bot.HasExited){Stop-Process -Id $bot.Id -Force -ErrorAction SilentlyContinue}}
    if($null -ne $serverProcess -and -not $serverProcess.HasExited){
        $actual=Get-Process -Id $serverProcess.Id -ErrorAction SilentlyContinue
        if($null -ne $actual -and $actual.Path -eq $serverExe){Stop-Process -Id $serverProcess.Id -Force}
    }
    $env:HEADLESS_CARD_PACK=$previous
}
