$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = Join-Path $run 'Probe/Probe.csproj'
$dll = Join-Path $run 'Probe/bin/Release/net11.0/Probe.dll'
$profile = Join-Path $run 'profiling'
dotnet build $project -c Release --no-restore | Set-Content (Join-Path $profile 'corpus-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler --output (Join-Path $profile 'corpus.nettrace') -- dotnet $dll profile-corpus 'I:/Images' 10 | Set-Content (Join-Path $profile 'corpus-collect.log')
if ($LASTEXITCODE -ne 0) { throw 'CPU profile failed.' }
dotnet-trace report (Join-Path $profile 'corpus.nettrace') topN -n 40 | Set-Content (Join-Path $profile 'corpus-top-exclusive.txt')
dotnet-trace report (Join-Path $profile 'corpus.nettrace') topN -n 40 --inclusive | Set-Content (Join-Path $profile 'corpus-top-inclusive.txt')
