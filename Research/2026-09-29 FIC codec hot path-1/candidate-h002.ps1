$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = Join-Path $run 'Probe/Probe.csproj'
$dll = Join-Path $run 'Probe/bin/Release/net11.0/Probe.dll'
$raw = Join-Path $run 'raw'
dotnet build $project -c Release --no-restore | Tee-Object (Join-Path $raw 'h002-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet test 'src/Tedd.FIC.Tests/Tedd.FIC.Tests.csproj' -c Release --no-restore | Tee-Object (Join-Path $raw 'h002-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'Correctness tests failed.' }
dotnet $dll hash | Set-Content (Join-Path $raw 'h002-hashes.txt')
if ($LASTEXITCODE -ne 0) { throw 'Hash run failed.' }
for ($i = 1; $i -le 3; $i++) {
    dotnet $dll measure | Set-Content (Join-Path $raw "h002-$i.csv")
    if ($LASTEXITCODE -ne 0) { throw "Candidate run $i failed." }
}
