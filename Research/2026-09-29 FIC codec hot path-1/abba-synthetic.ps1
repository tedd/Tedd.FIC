$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$variants = Join-Path (Get-Location) '.artifacts/ai/optimize-code/fic-hot-path/abba'
$raw = Join-Path $run 'raw'
foreach ($round in @(
    [pscustomobject]@{ Name = 'baseline'; Suffix = 'sa1' },
    [pscustomobject]@{ Name = 'candidate'; Suffix = 'sb1' },
    [pscustomobject]@{ Name = 'candidate'; Suffix = 'sb2' },
    [pscustomobject]@{ Name = 'baseline'; Suffix = 'sa2' })) {
    Write-Output "Running $($round.Name) $($round.Suffix)"
    dotnet (Join-Path $variants "$($round.Name)/Probe.dll") measure | Set-Content (Join-Path $raw "abba-$($round.Suffix).csv")
    if ($LASTEXITCODE -ne 0) { throw "ABBA $($round.Suffix) failed." }
}
