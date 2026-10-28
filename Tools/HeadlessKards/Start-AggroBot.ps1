param([string]$Configuration='bot-aggro.json')
$ErrorActionPreference='Stop'
$previous=$env:HEADLESS_CARD_PACK
Push-Location $PSScriptRoot
try {
    $env:HEADLESS_CARD_PACK=Join-Path $PSScriptRoot '..\CardExamples\pack-aggro.json'
    if(-not (Test-Path -LiteralPath 'out\HeadlessKards.dll')){throw 'Run Tools/HeadlessCore/Build.ps1 first'}
    dotnet out\HeadlessKards.dll bot $Configuration
    if($LASTEXITCODE -ne 0){throw 'Aggro bot stopped; see artifacts/live-aggro'}
} finally {$env:HEADLESS_CARD_PACK=$previous;Pop-Location}
