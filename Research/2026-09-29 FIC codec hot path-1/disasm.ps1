param([Parameter(Mandatory = $true)][string]$Prefix)
$ErrorActionPreference = 'Stop'
$run = $PSScriptRoot
$dll = Join-Path $run 'Probe/bin/Release/net11.0/Probe.dll'
$env:DOTNET_ReadyToRun = '0'
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_JitDisasm = '*Parse2*'
$env:DOTNET_JitStdOutFile = Join-Path $run "disassembly/$Prefix-parse2.asm"
New-Item -ItemType Directory -Force -Path (Join-Path $run 'disassembly') | Out-Null
dotnet $dll hash | Set-Content (Join-Path $run "disassembly/$Prefix-hash-run.txt")
if ($LASTEXITCODE -ne 0) { throw 'Disassembly workload failed.' }
