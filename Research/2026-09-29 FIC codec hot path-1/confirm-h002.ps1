$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'corpus.ps1') -Prefix h002b
if ($LASTEXITCODE -ne 0) { throw 'Corpus confirmation failed.' }
& (Join-Path $PSScriptRoot 'disasm.ps1') -Prefix h002
if ($LASTEXITCODE -ne 0) { throw 'Disassembly failed.' }
