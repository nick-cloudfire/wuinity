<#
.SYNOPSIS
    Builds everything the Unity project and the command line need from PREACT, in Release.

.DESCRIPTION
    PREACTcore -> WUInity\Assets\PREACT\Release\netstandard2.1\   engine DLLs and native wrappers that the
                                                                  Unity project uses
    PREACT     -> PREACT\PREACTexecute\bin\Release\net8.0\         PREACT.exe, the head-less scenario runner
    PREACTcli  -> PREACT\PREACTcli\bin\Release\net8.0\             PREACTcli.exe (build-case, converge-trigger, ...)

    The engine DLLs are not committed: run this once after cloning and again after pulling engine
    changes, before opening the Unity project. Needs the .NET 8 SDK (dotnet) on PATH. Works in
    Windows PowerShell 5.1 and PowerShell 7. build.sh is the Linux counterpart.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
#Requires -Version 5.0
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$UnityOut = [System.IO.Path]::Combine($Root, 'WUInity', 'Assets', 'PREACT', 'Release', 'netstandard2.1')
$ExeSuffix = ''
if ($env:OS -eq 'Windows_NT') { $ExeSuffix = '.exe' }
$PreactExe = [System.IO.Path]::Combine($Root, 'PREACT', 'PREACTexecute', 'bin', 'Release', 'net8.0', 'PREACT' + $ExeSuffix)
$CliExe = [System.IO.Path]::Combine($Root, 'PREACT', 'PREACTcli', 'bin', 'Release', 'net8.0', 'PREACTcli' + $ExeSuffix)

function Fail([string]$Message) {
    Write-Host ''
    Write-Host "BUILD FAILED: $Message" -ForegroundColor Red
    exit 1
}

function Get-RelativePath([string]$Path) {
    return $Path.Substring($Root.Length).TrimStart('\', '/')
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'dotnet was not found on PATH. Install the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0) and open a new terminal.'
}

$sdkOk = $false
foreach ($line in @(& dotnet --list-sdks)) {
    $major = 0
    if ([int]::TryParse(([string]$line).Split('.')[0], [ref]$major) -and $major -ge 8) { $sdkOk = $true }
}
if (-not $sdkOk) { Fail "no .NET SDK 8 or newer found ('dotnet --list-sdks'). Install the .NET 8 SDK." }

function Build-Project([string]$Project) {
    Write-Host ''
    Write-Host "==> dotnet build $Project -c Release" -ForegroundColor Cyan
    $path = [System.IO.Path]::Combine([string[]](@($Root) + $Project.Split('/')))
    & dotnet build $path -c Release -nologo
    if ($LASTEXITCODE -ne 0) {
        Fail ("$Project did not build (see the errors above). If MSBuild says a file is in use " +
              "(MSB3021/MSB3027), close the Unity editor, which keeps the native GDAL/NFDRS4 plugins " +
              "loaded, and run the script again.")
    }
}

# PREACTcore first: its Release output path is the Unity project (BaseOutputPath in PREACTcore.csproj).
# The other two reference it and only re-check it.
Build-Project 'PREACT/PREACTcore/PREACTcore.csproj'
Build-Project 'PREACT/PREACTexecute/PREACTexecute.csproj'
Build-Project 'PREACT/PREACTcli/PREACTcli.csproj'

# Every file Unity uses from the engine has a committed .meta beside it, so the metas are the list of
# what the build must have produced.
$missing = @()
if (Test-Path -LiteralPath $UnityOut) {
    foreach ($meta in @(Get-ChildItem -LiteralPath $UnityOut -Recurse -Force -Filter '*.meta')) {
        if ($meta.Name -like '*.pdb.meta') { continue }
        $asset = $meta.FullName.Substring(0, $meta.FullName.Length - 5)
        if (-not (Test-Path -LiteralPath $asset)) { $missing += (Get-RelativePath $asset) }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $UnityOut 'PREACTcore.dll'))) {
    $missing += (Get-RelativePath (Join-Path $UnityOut 'PREACTcore.dll'))
}
if ($missing.Count -gt 0) {
    foreach ($m in $missing) { Write-Host "  missing: $m" -ForegroundColor Red }
    Fail 'the Unity engine folder is incomplete: the files above have a .meta but were not built.'
}

# Files without a .meta are new build outputs; Unity writes their .meta on the next import, and that
# .meta should be committed so every checkout gets the same GUID.
foreach ($f in @(Get-ChildItem -LiteralPath $UnityOut -Recurse -Force -File)) {
    if ($f.Name -like '*.meta' -or $f.Name -like '*.pdb') { continue }
    if (-not (Test-Path -LiteralPath ($f.FullName + '.meta'))) {
        Write-Host ("note: " + (Get-RelativePath $f.FullName) + " has no .meta yet; commit the one Unity creates.") -ForegroundColor Yellow
    }
}

if (-not (Test-Path -LiteralPath $PreactExe)) { Fail "PREACT was built but $PreactExe is not there." }
if (-not (Test-Path -LiteralPath $CliExe)) { Fail "PREACTcli was built but $CliExe is not there." }

Write-Host ''
Write-Host 'Build succeeded.' -ForegroundColor Green
Write-Host "  Unity engine DLLs : $UnityOut"
Write-Host "  PREACT.exe        : $PreactExe"
Write-Host "  PREACTcli.exe     : $CliExe"
exit 0
