<#
.SYNOPSIS
    Runs the basic verification cases (Verification\) head-less and prints a PASS/FAIL table of measured against
    independently expected values. Builds nothing: run .\build.ps1 first. See docs\verification.md.

.DESCRIPTION
    .\verify.ps1 [--case 1,2,3,4] [--work DIR] [--preact PREACT.dll] [--cli PREACTcli.dll]
                 [--elmfire EXE] [--gdal BIN] [--sumo-home DIR] [--strict] [--list]

    Every argument is passed on to Verification\verify.py. It needs Python 3.8 or newer (the standard library only:
    any python.org install, the py launcher, or the Python that QGIS ships), the .NET 8 runtime, ELMFIRE (default:
    WUInity\Assets\ThirdParty\elmfire\build\windows\bin\elmfire.exe, or $env:ELMFIRE_EXE), GDAL's command-line tools
    (PATH, QGIS or OSGeo4W, or --gdal), and SUMO ($env:SUMO_HOME). The case folders must not have a space in their
    path when the campaign case runs (ELMFIRE hands paths to GDAL unquoted): pass --work C:\verify if the repository
    sits under such a path.

    Exit code: 0 all checks passed (known discrepancies, XFAIL, allowed), 1 a check failed, 2 a tool is missing.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $root 'Verification\verify.py'

function Test-Python([string[]]$command) {
    # The Microsoft Store's python.exe stub opens the Store instead of running; a version check tells them apart.
    try {
        $exe = $command[0]
        $rest = @()
        if ($command.Length -gt 1) { $rest = $command[1..($command.Length - 1)] }
        & $exe @rest -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' 2>$null | Out-Null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    }
}

$python = $null
foreach ($line in @('py -3', 'python', 'python3')) {
    # Split here rather than written as nested arrays: PowerShell flattens a one-element @(@('python')).
    $candidate = [string[]]($line -split ' ')
    if ((Get-Command $candidate[0] -ErrorAction SilentlyContinue) -and (Test-Python $candidate)) {
        $python = $candidate
        break
    }
}

if (-not $python) {
    # QGIS ships a full Python; the verification uses only its standard library, so PYTHONHOME is all it needs.
    $qgis = @()
    foreach ($base in @($env:ProgramFiles, 'C:\OSGeo4W', 'C:\OSGeo4W64')) {
        if ($base -and (Test-Path $base)) {
            $qgis += Get-ChildItem -Path $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'QGIS*' -or $_.Name -like 'OSGeo4W*' } |
                ForEach-Object { Get-ChildItem -Path (Join-Path $_.FullName 'apps') -Directory -Filter 'Python3*' -ErrorAction SilentlyContinue }
            $qgis += Get-ChildItem -Path (Join-Path $base 'apps') -Directory -Filter 'Python3*' -ErrorAction SilentlyContinue
        }
    }
    foreach ($dir in ($qgis | Sort-Object FullName -Descending)) {
        $exe = Join-Path $dir.FullName 'python.exe'
        if (Test-Path $exe) {
            $env:PYTHONHOME = $dir.FullName
            if (Test-Python ([string[]]@($exe))) { $python = [string[]]@($exe); break }
            Remove-Item Env:PYTHONHOME
        }
    }
}

if (-not $python) {
    Write-Error 'verify.ps1: Python 3.8 or newer was not found (py -3, python, python3, or a QGIS/OSGeo4W Python).'
    exit 2
}

$exe = $python[0]
$rest = @()
if ($python.Length -gt 1) { $rest = $python[1..($python.Length - 1)] }
& $exe @rest $script @args
exit $LASTEXITCODE
