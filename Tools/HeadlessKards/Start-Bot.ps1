param([string]$Configuration='bot-local.json')
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    if(-not (Test-Path -LiteralPath 'out\HeadlessKards.dll')) { throw '先运行 dotnet build -c Release -o out' }
    dotnet out\HeadlessKards.dll bot $Configuration
    if($LASTEXITCODE -ne 0){ throw "机器人停止，退出码 $LASTEXITCODE。请查看 artifacts/live 下的日志。" }
} finally { Pop-Location }
