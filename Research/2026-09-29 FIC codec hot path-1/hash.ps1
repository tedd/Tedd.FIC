param([Parameter(Mandatory = $true)][string]$Prefix)
$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$project = Join-Path $run 'Probe/Probe.csproj'
$dll = Join-Path $run 'Probe/bin/Release/net11.0/Probe.dll'
dotnet build $project -c Release --no-restore | Set-Content (Join-Path $run "raw/$Prefix-hash-build.log")
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet $dll hash | Set-Content (Join-Path $run "raw/$Prefix-hashes.txt")
if ($LASTEXITCODE -ne 0) { throw 'Hash run failed.' }
