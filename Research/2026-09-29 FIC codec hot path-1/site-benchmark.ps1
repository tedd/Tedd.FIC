$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = 'src/Tedd.FIC.Benchmark/Tedd.FIC.Benchmark.csproj'
$dll = 'src/Tedd.FIC.Benchmark/bin/Release/net11.0/Tedd.FIC.Benchmark.dll'
$raw = Join-Path $run 'raw'
dotnet build $project -c Release | Set-Content (Join-Path $raw 'site-benchmark-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
for ($i = 1; $i -le 3; $i++) {
    dotnet $dll 7 | Set-Content (Join-Path $raw "site-benchmark-$i.csv")
    if ($LASTEXITCODE -ne 0) { throw "Benchmark run $i failed." }
}
