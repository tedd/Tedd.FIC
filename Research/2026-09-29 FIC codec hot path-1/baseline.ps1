$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = Join-Path $run 'Probe/Probe.csproj'
$dll = Join-Path $run 'Probe/bin/Release/net11.0/Probe.dll'
$raw = Join-Path $run 'raw'
$profile = Join-Path $run 'profiling'
New-Item -ItemType Directory -Force -Path $raw, $profile | Out-Null
git rev-parse HEAD | Set-Content (Join-Path $run 'revision.txt')
git status --short | Add-Content (Join-Path $run 'revision.txt')
dotnet build $project -c Release | Tee-Object (Join-Path $raw 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
for ($i = 1; $i -le 3; $i++) {
    dotnet $dll measure | Set-Content (Join-Path $raw "baseline-$i.csv")
    if ($LASTEXITCODE -ne 0) { throw "Baseline run $i failed." }
}
dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler --output (Join-Path $profile 'baseline.nettrace') -- dotnet $dll profile | Tee-Object (Join-Path $profile 'collect.log')
if ($LASTEXITCODE -ne 0) { throw 'CPU profile failed.' }
