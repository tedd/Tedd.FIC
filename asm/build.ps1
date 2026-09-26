param([switch]$Regenerate)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$masm = 'C:\masm32\bin'
if (-not (Test-Path (Join-Path $masm 'ml.exe')) -or -not (Test-Path (Join-Path $masm 'poasm.exe'))) {
    throw 'C:\masm32\bin\ml.exe and poasm.exe are required.'
}
$vs = 'C:\Program Files\Microsoft Visual Studio\18\Insiders'
$msvc = Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $msvc) { throw 'Visual Studio C tools are required for MASM generation and linking.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build'

function Run-Vc([string]$arch, [string]$command) {
    $setup = if ($arch -eq 'x86') { 'vcvars32.bat' } else { 'vcvars64.bat' }
    $line = 'call "{0}" >nul && {1}' -f (Join-Path $vcvars $setup), $command
    & cmd.exe /d /s /c $line
    if ($LASTEXITCODE -ne 0) { throw "$arch tool failed with exit code $LASTEXITCODE" }
}

if ($Regenerate) {
    $source = Join-Path $root 'fic_native.c'
    $x86listing = Join-Path $out 'fic_x86_listing.asm'
    $x64listing = Join-Path $out 'fic_x64_listing.asm'
    $cl86 = Join-Path $msvc 'bin\Hostx86\x86\cl.exe'
    $cl64 = Join-Path $msvc 'bin\Hostx64\x64\cl.exe'
    # MASM32's ML 6.14 reassembles the unoptimized IA32 listing faithfully.
    Run-Vc 'x86' ('"{0}" /nologo /std:c11 /Od /arch:IA32 /GS- /W4 /c /FA /Fa"{1}" /Fo"{2}" "{3}"' -f
        $cl86, $x86listing, (Join-Path $out 'fic_x86_compiler.obj'), $source)
    Run-Vc 'x64' ('"{0}" /nologo /std:c11 /O2 /GS- /W4 /c /FA /Fa"{1}" /Fo"{2}" "{3}"' -f
        $cl64, $x64listing, (Join-Path $out 'fic_x64_compiler.obj'), $source)

    $x86 = (Get-Content -Raw $x86listing) -replace '(?im)^\s*include listing.inc\s*\r?\n', ''
    $x86 = [regex]::Replace($x86, '(?im)^\s*TITLE.*\r?\n', '')
    $x86 = [regex]::Replace($x86, '(?m)^; File .*fic_native\.c\r?$', '; File fic_native.c')
    $x86 = [regex]::Replace($x86, '(?m)[ \t]+(?=\r?$)', '').TrimEnd()
    Set-Content -LiteralPath (Join-Path $root 'fic_x86.asm') -Value $x86 -Encoding ascii

    # POASM 6.50 cannot assemble MSVC's IMAGEREL unwind sections; the DLL's
    # public functions use status returns and callbacks must not throw.
    $lines = Get-Content $x64listing
    $skip = $false
    $converted = foreach ($line in $lines) {
        if ($line -match '^(pdata|xdata)\s+SEGMENT') { $skip = $true; continue }
        if ($line -match '^(pdata|xdata)\s+ENDS') { $skip = $false; continue }
        if ($skip -or $line -match '^include listing.inc$' -or $line -match '^\s*ORG \$\+') { continue }
        $line -replace '^EXTRN\s+', 'EXTERNDEF '
    }
    $x64 = $converted -join "`r`n"
    $x64 = [regex]::Replace($x64, '(?m)^; File .*fic_native\.c\r?$', '; File fic_native.c')
    $x64 = [regex]::Replace($x64, '(?m)^\s*npad\s+\d+\s*\r?\n', '')
    $x64 = [regex]::Replace($x64, '\b([A-Za-z_$][A-Za-z0-9_$]*)\[([^\]]+)\]', '[$2+$1]')
    $x64 = [regex]::Replace($x64, '\$\[([^\]]+)\+T(\d+)\]', {
        param($m) '[' + $m.Groups[1].Value + '+$T' + $m.Groups[2].Value + ']'
    })
    $x64 = $x64.Replace('FLAT:', '')
    $x64 = [regex]::Replace($x64,
        '(?m)^(\s*)mov(\s+)(e[a-d]x|e[sd]i|e[bs]p|r\d+d),(\s*)(al|bl|cl|dl|sil|dil|bpl|spl|r\d+b)\b',
        '$1movzx$2$3,$4$5')
    $x64 = $x64.Replace("lea`trcx, OFFSET __ImageBase", "lea`trcx, [rip+gmshorts]")
    $x64 = $x64.Replace("lea`trdx, OFFSET __ImageBase", "lea`trdx, [rip+copies]")
    $x64 = $x64.Replace('[rcx+r10*4+gmshorts]', '[rcx+r10*4]')
    $x64 = $x64.Replace('[rdx+rcx*8+copies]', '[rdx+rcx*8]')
    $x64 = $x64.Replace('[rdx+rcx*8+4+copies]', '[rdx+rcx*8+4]')
    foreach ($symbol in 'copies', 'thresholds') {
        $x64 = $x64.Replace('OFFSET ' + $symbol, '[rip+' + $symbol + ']')
    }
    $x64 = [regex]::Replace($x64, '(?m)[ \t]+(?=\r?$)', '').TrimEnd()
    Set-Content -LiteralPath (Join-Path $root 'fic_x64.asm') -Value $x64 -Encoding ascii
}

& (Join-Path $masm 'ml.exe') /nologo /c /coff ('/Fo' + (Join-Path $out 'fic_x86.obj')) (Join-Path $root 'fic_x86.asm')
if ($LASTEXITCODE -ne 0) { throw 'x86 MASM assembly failed.' }
& (Join-Path $masm 'poasm.exe') /AAMD64 ('/Fo' + (Join-Path $out 'fic_x64.obj')) (Join-Path $root 'fic_x64.asm')
if ($LASTEXITCODE -ne 0) { throw 'x64 POASM assembly failed.' }

$link86 = Join-Path $msvc 'bin\Hostx86\x86\link.exe'
$link64 = Join-Path $msvc 'bin\Hostx64\x64\link.exe'
Run-Vc 'x86' ('"{0}" /NOLOGO /DLL /EXPORT:fic_decode /EXPORT:fic_encode /OUT:"{1}" "{2}"' -f
    $link86, (Join-Path $out 'fic_x86.dll'), (Join-Path $out 'fic_x86.obj'))
Run-Vc 'x64' ('"{0}" /NOLOGO /DLL /EXPORT:fic_decode /EXPORT:fic_encode /OUT:"{1}" "{2}"' -f
    $link64, (Join-Path $out 'fic_x64.dll'), (Join-Path $out 'fic_x64.obj'))
Write-Output "Built $out\fic_x86.dll and $out\fic_x64.dll"
