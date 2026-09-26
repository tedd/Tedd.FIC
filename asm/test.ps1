$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root 'build.ps1') -Regenerate
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }

$fixtures = Join-Path $env:TEMP ('fic_asm_fixtures_' + [guid]::NewGuid().ToString('N'))
$dotnetArgs = @('run', '--project', (Join-Path $root 'tests\AsmInterop.csproj'),
    '-c', 'Release', '--', (Join-Path $root 'bin\fic_x64.dll'), $fixtures)
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw 'x64 interop tests failed.' }

$vs = 'C:\Program Files\Microsoft Visual Studio\18\Insiders'
$msvc = Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars32.bat'
$cl = Join-Path $msvc 'bin\Hostx86\x86\cl.exe'
$smoke = Join-Path $root 'bin\x86_smoke.exe'
$source = Join-Path $root 'tests\x86_smoke.c'
$command = 'call "{0}" >nul && "{1}" /nologo /W4 /O2 /Fe:"{2}" "{3}"' -f
    $vcvars, $cl, $smoke, $source
& cmd.exe /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'x86 test harness build failed.' }
& $smoke (Join-Path $root 'bin\fic_x86.dll') $fixtures
if ($LASTEXITCODE -ne 0) { throw 'x86 interop tests failed.' }
