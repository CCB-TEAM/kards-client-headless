param([switch]$SkipTests)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    if(-not $SkipTests){
        cargo test --offline
        if($LASTEXITCODE -ne 0){throw 'Rust core tests failed'}
    }
    # Authoring VM is built separately; it is not part of the core deployment.
    cargo build --release --offline --features lua-authoring --bin card-lua --target-dir target-lua
    if($LASTEXITCODE -ne 0){throw 'Lua authoring build failed'}
    cargo build --release --offline --lib --bin headless-sim
    if($LASTEXITCODE -ne 0){throw 'Rust core build failed'}
    New-Item -ItemType Directory -Path out -Force | Out-Null
    Copy-Item -LiteralPath target\release\headless_core.dll -Destination out\headless_core.dll
    Copy-Item -LiteralPath target\release\headless-sim.exe -Destination out\headless-sim.exe
    Copy-Item -LiteralPath target-lua\release\card-lua.exe -Destination out\card-lua.exe
    dotnet build ..\HeadlessKards\HeadlessKards.csproj -c Release -o ..\HeadlessKards\out
    if($LASTEXITCODE -ne 0){throw 'Network host build failed'}
    dotnet run --project ..\CardExamples\CSharp\CardExamples.csproj -c Release -- ..\CardExamples\pack-csharp.json
    if($LASTEXITCODE -ne 0){throw 'C# card export failed'}
    & .\out\card-lua.exe ..\CardExamples\cards.lua ..\CardExamples\pack-lua.json
    if($LASTEXITCODE -ne 0){throw 'Lua card export failed'}
    & .\out\card-lua.exe ..\CardExamples\aggro.lua ..\CardExamples\pack-aggro.json
    if($LASTEXITCODE -ne 0){throw 'Aggro card export failed'}
    & ..\HeadlessKards\out\HeadlessKards.exe pack-validate ..\CardExamples\pack-csharp.json ..\CardExamples\pack-csharp.normalized.json
    if($LASTEXITCODE -ne 0){throw 'C# pack validation failed'}
    if(-not $SkipTests){
        & ..\HeadlessKards\out\HeadlessKards.exe script-test ..\CardExamples\pack-csharp.json ..\CardExamples\pack-lua.json |
            Tee-Object ..\HeadlessKards\artifacts\script-test-report.json
        if($LASTEXITCODE -ne 0){throw 'Script differential test failed'}
    }
} finally {Pop-Location}
