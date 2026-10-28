$ErrorActionPreference='Stop'
$directory=$PSScriptRoot
$temporaryListener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$temporaryListener.Start()
$port=([Net.IPEndPoint]$temporaryListener.LocalEndpoint).Port
$temporaryListener.Stop()
$address="http://127.0.0.1:$port"
$log=Join-Path $directory 'artifacts'
New-Item -ItemType Directory -Path $log -Force | Out-Null
$process=Start-Process -FilePath 'dotnet' -ArgumentList @((Join-Path $directory 'out\HeadlessKards.dll'),'serve',"$port") -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $log 'http-stdout.log') -RedirectStandardError (Join-Path $log 'http-stderr.log')
$checks=0
function Assert($Condition,[string]$Label){if(-not $Condition){throw "HTTP TEST FAILED: $Label"};$script:checks++}
function Request([string]$Path,[string]$Method='GET',$Body=$null){
    $parameters=@{Uri=$address+$Path;Method=$Method;TimeoutSec=10;SkipHttpErrorCheck=$true}
    if($null -ne $Body){$parameters.Body=($Body|ConvertTo-Json -Depth 12 -Compress);$parameters.ContentType='application/json'}
    return Invoke-WebRequest @parameters
}
try{
    $ready=$false
    for($i=0;$i -lt 40;$i++){
        if($process.HasExited){throw 'HTTP process exited early'}
        try{$health=Request '/health';if($health.StatusCode -eq 200){$ready=$true;break}}catch{}
        Start-Sleep -Milliseconds 250
    }
    Assert $ready 'Server startup'
    Assert (($health.Content|ConvertFrom-Json).native_client_protocol -eq $false) 'Explicit internal protocol'
    $create=Request '/v1/matches' 'POST' @{seed=42}
    Assert ($create.StatusCode -eq 200) 'Create match'
    $match=$create.Content|ConvertFrom-Json
    $path='/v1/matches/'+$match.id
    $initialHash=$match.state|ConvertTo-Json -Depth 12 -Compress
    $bad=Request ($path+'/actions') 'POST' @{expected_revision=999;command=@{side='left';kind='end_turn'}}
    Assert ($bad.StatusCode -eq 409) 'Reject stale revision'
    $state=(Request $path).Content|ConvertFrom-Json
    Assert (($state|ConvertTo-Json -Depth 12 -Compress) -ceq $initialHash) 'Rejected request preserves state'
    $bad=Request '/v1/matches' 'POST' @{seed=1;left_deck=@('unknown_card')}
    Assert ($bad.StatusCode -eq 409) 'Unsupported card rejection'
    $bad=Request ($path+'/actions') 'POST' @{expected_revision=0;command=@{side='left';kind='attack';card_id=999;target_id=888}}
    Assert ($bad.StatusCode -eq 409) 'Illegal action rejection'
    $bad=Request '/v1/matches' 'POST' @{seed=1;unexpected_field=$true}
    Assert ($bad.StatusCode -eq 400) 'Unknown request fields rejected'
    for($i=0;$i -lt 1000;$i++){
        $plan=(Request ($path+'/think')).Content|ConvertFrom-Json
        if($null -eq $plan.action){break}
        $apply=Request ($path+'/actions') 'POST' @{expected_revision=$plan.revision;command=$plan.action}
        if($apply.StatusCode -ne 200){throw "AI action failed: $($apply.Content)"}
        $state=($apply.Content|ConvertFrom-Json).state
        if($state.outcome){break}
    }
    Assert ([bool]$state.outcome) 'Complete AI match through HTTP'
    $replay=Request ($path+'/replay')
    $replayPath=Join-Path $log 'http-replay.json'
    $replay.Content | Set-Content -LiteralPath $replayPath -Encoding utf8NoBOM
    $verified=& dotnet (Join-Path $directory 'out\HeadlessKards.dll') replay $replayPath
    Assert ($LASTEXITCODE -eq 0) 'HTTP replay verified independently'
    Assert (((Request $path 'DELETE').StatusCode) -eq 200) 'Delete match'
    Assert (((Request $path).StatusCode) -eq 404) 'Deleted match absent'
    $fixturePath=Join-Path $log 'native-bootstrap.json'
    & dotnet (Join-Path $directory 'out\HeadlessKards.dll') wire-fixture $fixturePath
    if($LASTEXITCODE){throw 'Fixture generation failed'}
    $bootstrap=Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
    $created=Request '/v1/native/matches' 'POST' $bootstrap
    Assert ($created.StatusCode -eq 200) 'Native bootstrap imported'
    $native=$created.Content|ConvertFrom-Json
    $nativePath='/v1/native/matches/'+$native.id
    $bad=Request ($nativePath+'/actions') 'POST' @{a='invalid'}
    Assert ($bad.StatusCode -eq 409) 'Invalid encoded message rejected'
    $unchanged=(Request $nativePath).Content|ConvertFrom-Json
    Assert ($unchanged.state_hash -ceq $native.checkpoint.state_hash) 'Invalid packet preserves state'
    $plan=(Request ($nativePath+'/think')).Content|ConvertFrom-Json
    Assert ($plan.action.action_type -ceq 'XActionStartOfTurn') 'Native first start planned'
    $first=Request ($nativePath+'/actions') 'POST' @{a=$plan.a}
    Assert ($first.StatusCode -eq 200) 'Native start accepted'
    $retry=Request ($nativePath+'/actions') 'POST' @{a=$plan.a}
    Assert (($retry.Content|ConvertFrom-Json).state_hash -ceq ($first.Content|ConvertFrom-Json).state_hash) 'Native retry idempotent'
    for($i=0;$i -lt 1000;$i++){
        $plan=(Request ($nativePath+'/think')).Content|ConvertFrom-Json
        if($null -eq $plan.action){break}
        $apply=Request ($nativePath+'/actions') 'POST' @{a=$plan.a}
        if($apply.StatusCode -ne 200){throw "Native action failed: $($apply.Content)"}
        $checkpoint=$apply.Content|ConvertFrom-Json
        if($checkpoint.state.outcome){break}
    }
    Assert ([bool]$checkpoint.state.outcome) 'Encoded protocol HTTP match completes'
    $wireReplayPath=Join-Path $log 'http-wire-replay.json'
    (Request ($nativePath+'/replay')).Content | Set-Content -LiteralPath $wireReplayPath -Encoding utf8NoBOM
    $wireVerified=& dotnet (Join-Path $directory 'out\HeadlessKards.dll') wire-replay $wireReplayPath
    Assert ($LASTEXITCODE -eq 0) 'Protocol HTTP replay independently verified'
    Assert (((Request $nativePath 'DELETE').StatusCode) -eq 200) 'Protocol session deleted'
    $fresh=(Request '/v1/native/matches' 'POST' $bootstrap).Content|ConvertFrom-Json
    $freshPath='/v1/native/matches/'+$fresh.id
    $probe=$fresh.checkpoint
    $nativeCards=@(foreach($card in $probe.state.cards){
        $location=switch($card.zone){
            'support' {if($card.side -eq 'left'){'board_hqleft'}else{'board_hqright'}}
            'front' {'board_frontline'}
            'hand' {if($card.side -eq 'left'){'hand_left'}else{'hand_right'}}
            'deck' {if($card.side -eq 'left'){'deck_left'}else{'deck_right'}}
            'discard' {'discard'}
        }
        @{card_id=$card.id;name=$card.definition;attack=$card.attack;defense=$card.defense;side=$card.side;location=$location;location_number=$probe.positions.PSObject.Properties["$($card.id)"].Value}
    })
    $snapshot=@{cards=$nativeCards;kredits_left=$probe.state.players[0].kredits;kredits_right=$probe.state.players[1].kredits;max_kredits_left=$probe.state.players[0].max_kredits;max_kredits_right=$probe.state.players[1].max_kredits}
    $compare=Request ($freshPath+'/compare') 'POST' $snapshot
    Assert (($compare.Content|ConvertFrom-Json).equal -eq $true) 'Synthetic matching snapshot accepted'
    $snapshot.kredits_left=999
    $compare=Request ($freshPath+'/compare') 'POST' $snapshot
    Assert (($compare.Content|ConvertFrom-Json).equal -eq $false) 'Snapshot mismatch detected'
    Assert (((Request ($freshPath+'/think')).StatusCode) -eq 409) 'Desync stops AI planning'
    Assert (((Request ($freshPath+'/actions') 'POST' @{a=$plan.a}).StatusCode) -eq 409) 'Desync stops new actions'
    Assert (((Request $freshPath 'DELETE').StatusCode) -eq 200) 'Desynced session can be removed'
    @{passed=$checks;outcome=$state.outcome;turns=$state.turn;revision=$state.revision;replay=$replayPath;verification=($verified|ConvertFrom-Json);wireReplay=$wireReplayPath;wireVerification=($wireVerified|ConvertFrom-Json);nativeClientTested=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $log 'http-test-report.json') -Encoding utf8
    Write-Output "HEADLESS_HTTP_TEST_OK checks=$checks outcome=$($state.outcome) actions=$($state.revision)"
}finally{
    # Only terminate the test server started by this script.
    if(-not $process.HasExited){Stop-Process -Id $process.Id -ErrorAction SilentlyContinue}
}
