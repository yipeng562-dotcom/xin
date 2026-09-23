param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$receiverRoot = $PSScriptRoot
$outputRoot = Join-Path (Split-Path $receiverRoot -Parent) 'dist'
foreach($name in 'SnapshotAssistant','SnapshotAssistant64') {
    $project = Join-Path $receiverRoot "$name\$name.csproj"
    $tests = Join-Path $receiverRoot "$name.Tests\$name.Tests.csproj"
    & $Dotnet run --project $tests -c Release
    if($LASTEXITCODE -ne 0) { throw "$name regression tests failed" }
    & $Dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $outputRoot $name)
    if($LASTEXITCODE -ne 0) { throw "$name publish failed" }
}
