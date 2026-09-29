$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = Join-Path $run 'Probe/Probe.csproj'
$bin = Join-Path $run 'Probe/bin/Release/net11.0'
$variants = Join-Path (Get-Location) '.artifacts/ai/optimize-code/fic-hot-path/abba'
$raw = Join-Path $run 'raw'
foreach ($name in @('baseline', 'candidate')) { New-Item -ItemType Directory -Force -Path (Join-Path $variants $name) | Out-Null }
if (git diff -- src/Tedd.FIC/Ficq/Qp.cs src/Tedd.FIC/Ficq/R0.cs) { throw 'Codec source must be baseline before ABBA.' }
dotnet build $project -c Release --no-restore | Set-Content (Join-Path $raw 'abba-baseline-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Baseline build failed.' }
Copy-Item -Path (Join-Path $bin '*') -Destination (Join-Path $variants 'baseline') -Recurse -Force
git apply --ignore-space-change (Join-Path $raw 'h002.patch')
if ($LASTEXITCODE -ne 0) { throw 'Candidate patch failed.' }
dotnet build $project -c Release --no-restore | Set-Content (Join-Path $raw 'abba-h002-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }
Copy-Item -Path (Join-Path $bin '*') -Destination (Join-Path $variants 'candidate') -Recurse -Force
foreach ($round in @(
    [pscustomobject]@{ Name = 'baseline'; Suffix = 'a1' },
    [pscustomobject]@{ Name = 'candidate'; Suffix = 'b1' },
    [pscustomobject]@{ Name = 'candidate'; Suffix = 'b2' },
    [pscustomobject]@{ Name = 'baseline'; Suffix = 'a2' })) {
    $name, $suffix = $round.Name, $round.Suffix
    Write-Output "Running $name $suffix"
    dotnet (Join-Path $variants "$name/Probe.dll") corpus 'I:/Images' 10 | Set-Content (Join-Path $raw "abba-$suffix.csv")
    if ($LASTEXITCODE -ne 0) { throw "ABBA $suffix failed." }
}
