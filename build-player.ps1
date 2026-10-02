<#
.SYNOPSIS
    Builds WUInity as a standalone program: dist\WUInity\WUInity.exe, with PREACT.exe, PREACTcli and ELMFIRE beside it.

.DESCRIPTION
    1. Runs build.ps1 (skip with -SkipEngineBuild): the engine DLLs into the Unity project, PREACT.exe and
       PREACTcli.exe, all in Release.
    2. Runs Unity in batch mode (skip with -SkipPlayer): the editor script WUInity.Build.PlayerBuild
       (WUInity\Assets\WUInity\Editor\PlayerBuild.cs) builds the scenes enabled in the Build Settings into
       <Output>\WUInity.exe and <Output>\WUInity_Data. Unity's log goes to WUInity-build.log beside <Output>.
    3. Copies beside the player:
         PREACT\    PREACT.exe and PREACTcli.exe with their DLLs and the engine's native runtimes
         elmfire\   elmfire.exe with the DLLs beside it (impi.dll) and ELMFIRE's fuel model tables, when ELMFIRE
                    has been built (make_windows.bat) or -ElmfireExe names one
         docs\      the documentation, which Help > Getting started and Troubleshooting open
         README.txt what the folder holds and what else the machine needs
       and the engine's native runtimes into WUInity_Data\Managed\Runtimes, where the engine looks for them.

    SUMO, GDAL's command-line tools (QGIS or OSGeo4W), WindNinja and the .NET 8 runtime are not copied: the program
    finds them the way the editor does, or where Help > External tools and keys says (%APPDATA%\PREACT\tools.ini).
    docs\distribution.md has the details.

    Close the Unity editor first: Unity cannot open a project in batch mode while an editor has it open.
    Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Unity
    The Unity.exe to build with. Default: $env:UNITY_EXE, else Unity Hub's install of the project's version
    (WUInity\ProjectSettings\ProjectVersion.txt) under Program Files or the Hub's own install folder.

.PARAMETER Output
    The folder to build into. Default: dist\WUInity beside this script. An earlier build there is replaced; a
    folder holding anything else is refused.

.PARAMETER ElmfireExe
    The ELMFIRE executable to ship. Default: WUInity\Assets\ThirdParty\elmfire\build\windows\bin\elmfire.exe, the one
    make_windows.bat builds. It is shipped as elmfire\elmfire.exe whatever its name.

.PARAMETER SkipEngineBuild
    Use the engine, PREACT.exe and PREACTcli as they are built, without running build.ps1 first.

.PARAMETER SkipPlayer
    Keep the player already in -Output and only replace what is copied beside it (after an engine change).

.PARAMETER Development
    A development player (Unity's Development Build: a console window and stack traces with line numbers).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-player.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-player.ps1 -Unity "D:\Unity\6000.3.15f1\Editor\Unity.exe"
#>
#Requires -Version 5.0
[CmdletBinding()]
param(
    [string]$Unity = '',
    [string]$Output = '',
    [string]$ElmfireExe = '',
    [switch]$SkipEngineBuild,
    [switch]$SkipPlayer,
    [switch]$Development
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Project = [System.IO.Path]::Combine($Root, 'WUInity')
$OnWindows = ($env:OS -eq 'Windows_NT')
$ExeSuffix = ''
if ($OnWindows) { $ExeSuffix = '.exe' }
$UnityEngineDir = [System.IO.Path]::Combine($Project, 'Assets', 'PREACT', 'Release', 'netstandard2.1')
$PreactBin = [System.IO.Path]::Combine($Root, 'PREACT', 'PREACTexecute', 'bin', 'Release', 'net8.0')
$CliBin = [System.IO.Path]::Combine($Root, 'PREACT', 'PREACTcli', 'bin', 'Release', 'net8.0')
$Warnings = New-Object System.Collections.Generic.List[string]

function Fail([string]$Message) {
    Write-Host ''
    Write-Host "BUILD FAILED: $Message" -ForegroundColor Red
    exit 1
}

function Warn([string]$Message) {
    Write-Host "warning: $Message" -ForegroundColor Yellow
    $Warnings.Add($Message)
}

function Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Full([string]$Path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Same-Path([string]$A, [string]$B) {
    $a1 = [System.IO.Path]::GetFullPath($A).TrimEnd('\', '/')
    $b1 = [System.IO.Path]::GetFullPath($B).TrimEnd('\', '/')
    if ($OnWindows) { return [string]::Equals($a1, $b1, [System.StringComparison]::OrdinalIgnoreCase) }
    return [string]::Equals($a1, $b1, [System.StringComparison]::Ordinal)
}

function Is-Inside([string]$Path, [string]$Folder) {
    $p = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $f = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $comparison = [System.StringComparison]::Ordinal
    if ($OnWindows) { $comparison = [System.StringComparison]::OrdinalIgnoreCase }
    return $p.StartsWith($f, $comparison)
}

# One argument as the Windows command line parser (CommandLineToArgvW, and .NET everywhere) reads it back.
function Quote-Argument([string]$Text) {
    if ($Text.Length -gt 0 -and $Text -notmatch '[\s"]') { return $Text }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $Text.ToCharArray()) {
        if ($ch -eq '\') { $slashes++; continue }
        if ($ch -eq '"') {
            [void]$sb.Append('\' * (2 * $slashes + 1))
            [void]$sb.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$sb.Append('\' * $slashes); $slashes = 0 }
        [void]$sb.Append($ch)
    }
    [void]$sb.Append('\' * (2 * $slashes))
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Get-RelativePath([string]$Path, [string]$Base) {
    return $Path.Substring($Base.TrimEnd('\', '/').Length).TrimStart('\', '/')
}

# Copies every file under $From into $To, keeping the folders. A file already there with other contents is a
# conflict (two programs needing two versions of one DLL), which is refused rather than overwritten.
function Copy-Merged([string]$From, [string]$To) {
    $conflicts = @()
    $count = 0
    foreach ($f in @(Get-ChildItem -LiteralPath $From -Recurse -Force -File)) {
        $relative = Get-RelativePath $f.FullName $From
        $target = [System.IO.Path]::Combine($To, $relative)
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $h1 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
            $h2 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($h1 -ne $h2) { $conflicts += $relative }
            continue
        }
        $folder = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $folder)) { [void](New-Item -ItemType Directory -Path $folder -Force) }
        Copy-Item -LiteralPath $f.FullName -Destination $target
        $count++
    }
    if ($conflicts.Count -gt 0) {
        Fail ("$From and what is already in $To have different versions of: " + ($conflicts -join ', ') +
              '. Build both with the same engine (run build.ps1, or this script without -SkipEngineBuild).')
    }
    return $count
}

function Find-Unity {
    if ($Unity) {
        $u = Full $Unity
        if (Test-Path -LiteralPath $u -PathType Leaf) { return $u }
        Fail "-Unity names $u, which is not there."
    }
    if ($env:UNITY_EXE) {
        if (Test-Path -LiteralPath $env:UNITY_EXE -PathType Leaf) { return $env:UNITY_EXE }
        Fail "UNITY_EXE names $($env:UNITY_EXE), which is not there."
    }

    $versionFile = [System.IO.Path]::Combine($Project, 'ProjectSettings', 'ProjectVersion.txt')
    $version = $null
    foreach ($line in @(Get-Content -LiteralPath $versionFile)) {
        if ($line -match '^m_EditorVersion:\s*(\S+)') { $version = $Matches[1] }
    }
    if (-not $version) { Fail "could not read the project's Unity version from $versionFile." }

    $candidates = @()
    foreach ($programFiles in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432)) {
        if ($programFiles) { $candidates += [System.IO.Path]::Combine($programFiles, 'Unity', 'Hub', 'Editor', $version, 'Editor', 'Unity.exe') }
    }
    # Unity Hub's "Installs location" when it is not the default, a JSON string such as "D:\\Unity".
    if ($env:APPDATA) {
        $secondary = [System.IO.Path]::Combine($env:APPDATA, 'UnityHub', 'secondaryInstallPath.json')
        if (Test-Path -LiteralPath $secondary) {
            $folder = (Get-Content -LiteralPath $secondary -Raw).Trim().Trim('"').Replace('\\', '\')
            if ($folder) { $candidates += [System.IO.Path]::Combine($folder, $version, 'Editor', 'Unity.exe') }
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }
    $looked = 'nowhere: there is no Program Files folder'
    if ($candidates.Count -gt 0) { $looked = $candidates -join ', ' }
    Fail ("Unity $version was not found (looked for $looked). Pass -Unity <path to Unity.exe>, " +
          'or set UNITY_EXE, or install that version with Unity Hub.')
}

# ------------------------------------------------------------------ the output folder
if ($Output) { $Output = Full $Output } else { $Output = [System.IO.Path]::Combine($Root, 'dist', 'WUInity') }
$Output = [System.IO.Path]::GetFullPath($Output).TrimEnd('\', '/')
$PlayerExe = [System.IO.Path]::Combine($Output, 'WUInity.exe')
$PlayerData = [System.IO.Path]::Combine($Output, 'WUInity_Data')

if ((Same-Path $Output $Root) -or (Is-Inside $Root $Output)) { Fail "-Output $Output would hold the repository itself." }
if (Is-Inside $Output (Join-Path $Project 'Assets')) { Fail "-Output $Output is inside WUInity\Assets, which Unity would import." }

if ($SkipPlayer) {
    if (-not (Test-Path -LiteralPath $PlayerExe) -or -not (Test-Path -LiteralPath $PlayerData)) {
        Fail "-SkipPlayer needs an earlier build in $Output (WUInity.exe and WUInity_Data), and there is none."
    }
} elseif (Test-Path -LiteralPath $Output) {
    $items = @(Get-ChildItem -LiteralPath $Output -Force)
    $earlier = (Test-Path -LiteralPath $PlayerExe) -or (Test-Path -LiteralPath $PlayerData)
    if ($items.Count -gt 0 -and -not $earlier) {
        Fail "$Output is not empty and holds no earlier WUInity build; empty it, or pass another -Output."
    }
}

# ------------------------------------------------------------------ 1. the engine, PREACT.exe and PREACTcli
if (-not $SkipEngineBuild) {
    Step 'build.ps1 (engine, PREACT.exe, PREACTcli; Release)'
    & ([System.IO.Path]::Combine($Root, 'build.ps1'))
    if ($LASTEXITCODE -ne 0) { Fail 'build.ps1 failed; see above.' }
}
foreach ($need in @(
        @([System.IO.Path]::Combine($UnityEngineDir, 'PREACTcore.dll'), 'the engine for Unity'),
        @([System.IO.Path]::Combine($PreactBin, 'PREACT.dll'), 'PREACT'),
        @([System.IO.Path]::Combine($CliBin, 'PREACTcli.dll'), 'PREACTcli'))) {
    if (-not (Test-Path -LiteralPath $need[0])) { Fail "$($need[1]) is not built ($($need[0]) is missing): run build.ps1, or this script without -SkipEngineBuild." }
}

# ------------------------------------------------------------------ 2. the player
if (-not $SkipPlayer) {
    $UnityExe = Find-Unity
    Step "Unity: building the player with $UnityExe"

    # The editor holds Temp\UnityLockfile open while it has the project; batch mode would then refuse to start.
    $lock = [System.IO.Path]::Combine($Project, 'Temp', 'UnityLockfile')
    if (Test-Path -LiteralPath $lock) {
        try {
            $stream = [System.IO.File]::Open($lock, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Close()
        } catch {
            Fail 'a Unity editor has WUInity open (WUInity\Temp\UnityLockfile is in use). Close it and run this again.'
        }
    }

    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
    [void](New-Item -ItemType Directory -Path $Output -Force)
    $log = [System.IO.Path]::Combine((Split-Path -Parent $Output), 'WUInity-build.log')
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }

    $unityArgs = @('-batchmode', '-quit', '-buildTarget', 'Win64', '-projectPath', $Project, '-logFile', $log,
                   '-executeMethod', 'WUInity.Build.PlayerBuild.BuildFromCommandLine', '-buildOutput', $PlayerExe)
    if ($Development) { $unityArgs += '-developmentBuild' }

    # Started as a process and waited on by itself: Unity.exe is a windowed program, which the call operator does not
    # wait for, and Start-Process -Wait also waits for the licensing client Unity leaves running.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $UnityExe
    $psi.Arguments = (($unityArgs | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $psi.UseShellExecute = $false
    Write-Host "  log: $log (the first build imports the whole project and takes a while)"
    $started = Get-Date
    $process = [System.Diagnostics.Process]::Start($psi)
    while (-not $process.WaitForExit(30000)) {
        Write-Host ('  ... {0:N0} min' -f ((Get-Date) - $started).TotalMinutes)
    }
    $code = $process.ExitCode

    $lines = @()
    if (Test-Path -LiteralPath $log) { $lines = @(Get-Content -LiteralPath $log) }
    foreach ($line in $lines) {
        if ($line -match 'PlayerBuild:') { Write-Host "  $line" }
    }
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $PlayerExe) -or -not (Test-Path -LiteralPath $PlayerData)) {
        $errors = @($lines | Where-Object { $_ -match 'error CS\d+|executeMethod|Aborting batchmode|Build Finished, Result: Failure|another Unity instance' })
        foreach ($e in ($errors | Select-Object -Last 25)) { Write-Host "  $e" -ForegroundColor Red }
        if ($errors.Count -eq 0) { foreach ($e in ($lines | Select-Object -Last 25)) { Write-Host "  $e" } }
        Fail "Unity exited with $code and $PlayerExe was not built; the full log is $log."
    }
    Write-Host ('  built in {0:N1} min' -f ((Get-Date) - $started).TotalMinutes)
}

# ------------------------------------------------------------------ 3. what goes beside it
foreach ($folder in @('PREACT', 'elmfire', 'docs')) {
    $path = [System.IO.Path]::Combine($Output, $folder)
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

Step 'PREACT.exe and PREACTcli'
$preactOut = [System.IO.Path]::Combine($Output, 'PREACT')
[void](New-Item -ItemType Directory -Path $preactOut -Force)
$n = (Copy-Merged $PreactBin $preactOut)
$n += (Copy-Merged $CliBin $preactOut)
Write-Host "  $n files into $preactOut"

Step 'the engine runtimes for the player'
# The engine puts Runtimes\Native\<library>\x64 beside PREACTcore.dll on the library search path (NativeLibraries.
# EngineRuntimeFolders); in a player PREACTcore.dll is in WUInity_Data\Managed. Unity copies the same DLLs into
# WUInity_Data\Plugins as native plugins; this makes the engine's own lookup work whatever the plugin settings say.
$managed = [System.IO.Path]::Combine($PlayerData, 'Managed')
$nativeFrom = [System.IO.Path]::Combine($UnityEngineDir, 'Runtimes', 'Native')
if (-not (Test-Path -LiteralPath $managed)) { Fail "$managed is not there: the player build is incomplete." }
$nativeTo = [System.IO.Path]::Combine($managed, 'Runtimes', 'Native')
if (Test-Path -LiteralPath $nativeTo) { Remove-Item -LiteralPath $nativeTo -Recurse -Force }
$copied = 0
foreach ($f in @(Get-ChildItem -LiteralPath $nativeFrom -Recurse -File -Filter '*.dll')) {
    $target = [System.IO.Path]::Combine($nativeTo, (Get-RelativePath $f.FullName $nativeFrom))
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
    Copy-Item -LiteralPath $f.FullName -Destination $target
    $copied++
}
Write-Host "  $copied DLLs into $nativeTo"
$plugins = [System.IO.Path]::Combine($PlayerData, 'Plugins', 'x86_64')
foreach ($wrap in @('gdal_wrap.dll', 'ogr_wrap.dll', 'osr_wrap.dll', 'NFDRS4core.dll')) {
    if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($plugins, $wrap)))) {
        Warn "the player has no $wrap in WUInity_Data\Plugins\x86_64; the engine will load the copy in Managed\Runtimes."
    }
}

Step 'ELMFIRE'
if (-not $ElmfireExe) {
    $os = 'linux'
    if ($OnWindows) { $os = 'windows' }
    $ElmfireExe = [System.IO.Path]::Combine($Project, 'Assets', 'ThirdParty', 'elmfire', 'build', $os, 'bin', 'elmfire' + $ExeSuffix)
} else {
    $ElmfireExe = Full $ElmfireExe
}
$elmfireShipped = $false
if (Test-Path -LiteralPath $ElmfireExe -PathType Leaf) {
    $elmfireOut = [System.IO.Path]::Combine($Output, 'elmfire')
    [void](New-Item -ItemType Directory -Path $elmfireOut -Force)
    $bin = Split-Path -Parent $ElmfireExe
    # Shipped under the name the engine looks for beside a standalone build (ElmfireCoupling.ResolveExecutable).
    Copy-Item -LiteralPath $ElmfireExe -Destination ([System.IO.Path]::Combine($elmfireOut, 'elmfire' + $ExeSuffix))
    $dlls = @(Get-ChildItem -LiteralPath $bin -File -Filter '*.dll')
    foreach ($d in $dlls) { Copy-Item -LiteralPath $d.FullName -Destination $elmfireOut }
    if ($OnWindows -and -not ($dlls | Where-Object { $_.Name -ieq 'impi.dll' })) {
        Warn ("there is no impi.dll beside $ElmfireExe (make_windows.bat copies it there). Without it the shipped " +
              'elmfire.exe only starts on a machine with Intel oneAPI or the Intel MPI runtime installed.')
    }

    # ELMFIRE's default fuel model tables, which a case build copies into a case that has none: build\source of the
    # ELMFIRE tree an executable in build\<os>\bin belongs to, else beside the executable.
    $source = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($bin, '..', '..', 'source'))
    $tables = @()
    if (Test-Path -LiteralPath $source) { $tables = @(Get-ChildItem -LiteralPath $source -File -Filter '*.csv') }
    if ($tables.Count -eq 0) { $tables = @(Get-ChildItem -LiteralPath $bin -File -Filter '*.csv') }
    foreach ($t in $tables) { Copy-Item -LiteralPath $t.FullName -Destination $elmfireOut }
    if (-not ($tables | Where-Object { $_.Name -ieq 'fuel_models.csv' })) {
        Warn "ELMFIRE's fuel_models.csv was not found in $source; a case built without one gets ELMFIRE's built-in table."
    }
    Write-Host ("  elmfire$ExeSuffix, " + ((@($dlls | ForEach-Object { $_.Name }) + @($tables | ForEach-Object { $_.Name })) -join ', ') + " into $elmfireOut")
    $elmfireShipped = $true
} else {
    Warn ("ELMFIRE is not built ($ElmfireExe is not there), so none is shipped: the program then uses the ELMFIRE " +
          'named under Help > External tools and keys. Build it with make_windows.bat, or pass -ElmfireExe.')
}

Step 'docs and README.txt'
$docsFrom = [System.IO.Path]::Combine($Root, 'docs')
$docsOut = [System.IO.Path]::Combine($Output, 'docs')
Copy-Item -LiteralPath $docsFrom -Destination $docsOut -Recurse
foreach ($top in @('README.md', 'CHANGELOG.md', 'LICENSE.txt', 'GNU_GPLv3.txt')) {
    $p = [System.IO.Path]::Combine($Root, $top)
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $docsOut }
}

$commit = 'unknown'
try {
    $c = & git -C $Root rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $c) { $commit = [string]$c }
} catch { }
$elmfireLine = '  elmfire\                     ELMFIRE (elmfire.exe, impi.dll, its fuel model tables)'
if (-not $elmfireShipped) { $elmfireLine = '  (no elmfire\: ELMFIRE was not built when this was made; name one under Help > External tools and keys)' }
$readme = @(
    "WUInity / PREACT, standalone build (commit $commit, built $(Get-Date -Format 'yyyy-MM-dd HH:mm'))",
    '',
    'Start WUInity.exe. Help > Getting started opens docs\getting-started.md; docs\distribution.md describes this folder.',
    '',
    'In this folder',
    '  WUInity.exe, WUInity_Data\   the visualizer (Unity player)',
    '  PREACT\PREACT.exe            the head-less scenario runner',
    '  PREACT\PREACTcli.exe         build-case, converge-trigger (trigger campaigns), global-gpw-to-pop',
    $elmfireLine,
    '  docs\                        the documentation',
    '',
    'Not in this folder - install them on this machine',
    '  .NET 8 Runtime (x64)         PREACT.exe and PREACTcli.exe, so campaigns, need it ("dotnet --list-runtimes")',
    '  SUMO 1.22                    traffic, and the GDAL library (gdal.dll) the engine loads from its bin',
    '  QGIS or OSGeo4W              GDAL''s command-line tools, which ELMFIRE runs',
    '  WindNinja                    terrain-resolved wind (without it a case gets one wind for the whole domain)',
    '',
    'Help > External tools and keys shows which of each is used and where it was found, and takes a path for each.',
    'Those paths are saved per user in %APPDATA%\PREACT\tools.ini, which PREACT.exe and PREACTcli read too.'
)
Set-Content -LiteralPath ([System.IO.Path]::Combine($Output, 'README.txt')) -Value $readme -Encoding UTF8

# ------------------------------------------------------------------ done
$size = (Get-ChildItem -LiteralPath $Output -Recurse -Force -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host 'Build succeeded.' -ForegroundColor Green
Write-Host "  program   : $PlayerExe"
Write-Host "  PREACT    : $([System.IO.Path]::Combine($preactOut, 'PREACT' + $ExeSuffix)), $([System.IO.Path]::Combine($preactOut, 'PREACTcli' + $ExeSuffix))"
if ($elmfireShipped) { Write-Host "  ELMFIRE   : $([System.IO.Path]::Combine($Output, 'elmfire', 'elmfire' + $ExeSuffix))" }
Write-Host ('  size      : {0:N0} MB' -f ($size / 1MB))
if ($Warnings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($Warnings.Count) warning(s):" -ForegroundColor Yellow
    foreach ($w in $Warnings) { Write-Host "  - $w" -ForegroundColor Yellow }
}
exit 0
