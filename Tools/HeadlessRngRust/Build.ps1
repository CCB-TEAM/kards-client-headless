$ErrorActionPreference='Stop'
& cargo test --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --offline
if($LASTEXITCODE){throw 'Rust unit tests failed'}
& cargo build --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --release --offline
if($LASTEXITCODE){throw 'Rust library build failed'}
& dotnet build (Join-Path $PSScriptRoot '..\HeadlessKards\HeadlessKards.csproj') -c Release -o (Join-Path $PSScriptRoot '..\HeadlessKards\out')
if($LASTEXITCODE){throw 'Headless client build failed'}
