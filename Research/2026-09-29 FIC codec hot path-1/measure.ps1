param([Parameter(Mandatory = $true)][string]$Prefix)
$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = Join-Path $run 'Probe/Probe.csproj'
$dll = Join-Path $run 'Probe/bin/Release/net11.0/Probe.dll'
$raw = Join-Path $run 'raw'
dotnet build $project -c Release --no-restore | Tee-Object (Join-Path $raw "$Prefix-build.log")
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
for ($i = 1; $i -le 3; $i++) {
    dotnet $dll measure | Set-Content (Join-Path $raw "$Prefix-$i.csv")
    if ($LASTEXITCODE -ne 0) { throw "Measurement run $i failed." }
}
