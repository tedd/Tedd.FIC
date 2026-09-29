param([Parameter(Mandatory = $true)][string]$Prefix)
$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = Join-Path $run 'Probe/Probe.csproj'
$dll = Join-Path $run 'Probe/bin/Release/net11.0/Probe.dll'
$raw = Join-Path $run 'raw'
dotnet build $project -c Release | Set-Content (Join-Path $raw "$Prefix-corpus-build.log")
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet $dll corpus 'I:/Images' 10 | Set-Content (Join-Path $raw "$Prefix-corpus.csv")
if ($LASTEXITCODE -ne 0) { throw 'Corpus run failed.' }
