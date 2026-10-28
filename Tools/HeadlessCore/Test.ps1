$ErrorActionPreference='Stop'
$previousCardPack=$env:HEADLESS_CARD_PACK
$previousRulesBackend=$env:HEADLESS_RULES_BACKEND
Remove-Item Env:HEADLESS_CARD_PACK -ErrorAction SilentlyContinue
$env:HEADLESS_RULES_BACKEND='rust'
Push-Location (Join-Path $PSScriptRoot '..\..')
try {
    $exe='.\Tools\HeadlessKards\out\HeadlessKards.exe'
    foreach($mode in @('selftest','rules-parity-test','protocol-test')) {
        & $exe $mode | Tee-Object "Tools\HeadlessKards\artifacts\rust-core-$mode-report.json"
        if($LASTEXITCODE -ne 0){throw "Failed: $mode"}
    }
    & $exe script-test Tools\CardExamples\pack-csharp.json Tools\CardExamples\pack-lua.json |
        Tee-Object Tools\HeadlessKards\artifacts\script-test-report.json
    if($LASTEXITCODE -ne 0){throw 'Cross-language script test failed'}
    & $exe aggro-test Tools\CardExamples\pack-aggro.json 25
    if($LASTEXITCODE -ne 0){throw 'Aggro protocol/replay test failed'}
    $env:HEADLESS_CARD_PACK=(Join-Path (Get-Location) 'Tools\CardExamples\pack-lua.json')
    try {
        & $exe selfplay-config Tools\CardExamples\match.json Tools\HeadlessKards\artifacts\script-demo-replay.json
        if($LASTEXITCODE -ne 0){throw 'Script selfplay failed'}
        & $exe replay Tools\HeadlessKards\artifacts\script-demo-replay.json
        if($LASTEXITCODE -ne 0){throw 'Script replay failed'}
    } finally {Remove-Item Env:HEADLESS_CARD_PACK}
    pwsh -NoProfile -File Tools\HeadlessKards\Smoke-Test.ps1
    if($LASTEXITCODE -ne 0){throw 'Rust-backed HTTP test failed'}
    python Tools\HeadlessCore\Test-Standalone.py
    if($LASTEXITCODE -ne 0){throw 'Standalone Rust test failed'}
} finally {
    $env:HEADLESS_CARD_PACK=$previousCardPack
    $env:HEADLESS_RULES_BACKEND=$previousRulesBackend
    Pop-Location
}
