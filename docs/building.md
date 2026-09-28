# Building

This page covers building the engine, the two console tools and ELMFIRE, what each build puts where, and the
Linux set-up. For first use of the GUI, see [Getting started](getting-started.md).

## Repository layout

| Path | Contents |
|---|---|
| `build.ps1`, `build.sh` | The build scripts (Windows, Linux). |
| `PREACT/PREACTcore/` | The engine, netstandard2.1. Its Release build goes into the Unity project. |
| `PREACT/PREACTexecute/` | `PREACT.exe`, the head-less scenario runner (net8.0). |
| `PREACT/PREACTcli/` | `PREACTcli.exe`: `build-case`, `converge-trigger`, `global-gpw-to-pop` (net8.0). |
| `PREACT/kPERILcore/` | The trigger-boundary engine, built from source with PREACTcore. |
| `PREACT/PREACTtests/` | A console test runner (no test framework). |
| `PREACT/PREACT.sln` | The five projects above. |
| `WUInity/` | The Unity project. First-party scripts are under `Assets/WUInity/`. |
| `WUInity/Assets/ThirdParty/elmfire/` | ELMFIRE, a git submodule (branch `ELMFIRE-WUINITY`). |
| `WUInity/Assets/ThirdParty/Nelson-Dead-Fuel-Moisture/`, `WildfireAV/` | Submodules. The engine and CLI do not build against them; Unity compiles the Nelson sources it finds under `Assets/`. |
| `Examples/` | Two scenarios that run as shipped — see [Examples](examples.md). |
| `docs/` | This documentation. |

After cloning, fetch the submodules. The ELMFIRE gitlink is pinned at **a7fb9d6**:

```sh
git submodule update --init --recursive
git -C WUInity/Assets/ThirdParty/elmfire log -1 --oneline    # a7fb9d6 Added a new output option, DUMP_MIDFLAME_WINDSPEED ...
```

## The build scripts

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1     # Windows PowerShell 5.1 or PowerShell 7
```
```sh
./build.sh                                               # Linux
```

Both do the same thing:

1. Check that `dotnet` is on `PATH` and that an SDK 8 or newer is installed.
2. Build `PREACTcore`, then `PREACTexecute`, then `PREACTcli`, all with `-c Release`.
3. Check that every file the Unity project expects was produced. The expected set is the committed `.meta`
   files under `WUInity/Assets/PREACT/Release/netstandard2.1/`: each `.meta` must have its file beside it.
4. Print a `note:` for a new build output that has no `.meta` yet (commit the `.meta` Unity creates for it).
5. Check that the two executables exist and print where everything went.

On any failure they print `BUILD FAILED: <reason>` and exit 1.

| Output | Where |
|---|---|
| Engine for Unity (`PREACTcore.dll`, `kPERILcore.dll`, the GDAL, NFDRS4 and FOFEM native wrappers, `OpenMeteo.dll`, `PREACTcore.deps.json`) | `WUInity/Assets/PREACT/Release/netstandard2.1/` |
| `PREACT.exe` (`PREACT` on Linux; `dotnet PREACT.dll` everywhere) | `PREACT/PREACTexecute/bin/Release/net8.0/` |
| `PREACTcli.exe` (`PREACTcli` on Linux) | `PREACT/PREACTcli/bin/Release/net8.0/` |

**The engine DLLs are not committed.** Only their `.meta` files are, so that Unity keeps the same GUIDs and
plugin import settings on every checkout. So:

- run the script **before opening Unity** after cloning, and again after every pull that changes `PREACT/`;
  Unity opened without them reports compile errors in the WUInity scripts;
- if Unity was opened first, close it, run the script, and open it again;
- if MSBuild reports a file in use (`MSB3021`/`MSB3027`), close the Unity editor — it keeps the native GDAL and
  NFDRS4 plugins loaded — and run the script again;
- `the Unity engine folder is incomplete: the files above have a .meta but were not built` means the engine's
  output set changed without its `.meta` files following. If you removed a file from the engine's output on
  purpose, delete its `.meta` too.

A Release folder left over from an older checkout can still hold files the engine no longer builds (for
example `template.data` or the `gdalconst` wrappers). Delete them by hand; Unity would otherwise keep them and
write untracked `.meta` files beside them.

## Building the projects one by one

```sh
dotnet build PREACT/PREACTcore/PREACTcore.csproj -c Release       # engine; Release output goes to the Unity project
dotnet build PREACT/PREACTexecute/PREACTexecute.csproj -c Release
dotnet build PREACT/PREACTcli/PREACTcli.csproj -c Release
dotnet build PREACT/PREACT.sln                                     # all five, Debug
```

PREACTcore writes its **Release** output into `WUInity/Assets/PREACT/Release/` (`BaseOutputPath` in its
`.csproj`) and its **Debug** output into `PREACT/PREACTcore/bin/`. A Debug build therefore does not update what
Unity uses.

All five projects build with warnings treated as errors.

The tests:

```sh
dotnet build PREACT/PREACTtests/PREACTtests.csproj
dotnet PREACT/PREACTtests/bin/Debug/net8.0/PREACTtests.dll [--no-examples] [--filter <text>] [--verbose] [file.wui ...]
```

They run format, engine, pipeline, trigger and CLI tests without SUMO or case data, then load, save and reload
every `Examples/**/*.wui` (and any `.wui` you name) and fail if a key is lost or the writer is not
idempotent. Exit code 0 when everything passes, 1 otherwise.

### What the engine brings with it

| Library | Where | Notes |
|---|---|---|
| GDAL C# wrappers (`gdal_csharp`, `ogr_csharp`, `osr_csharp`) and their native glue (`gdal_wrap`, `ogr_wrap`, `osr_wrap`) | `PREACT/PREACTcore/Runtimes/Native/GDAL/` | The glue loads the native GDAL itself: `gdal.dll` on Windows, which comes from your SUMO install's `bin` (the engine appends it to `PATH`); `libgdal.so.36`, i.e. GDAL 3.10, on Linux. |
| NFDRS4 (`NFDRS4core`) | `Runtimes/Native/NFDRS4/` | Live fuel moisture and the Nelson dead fuel moisture sticks for ELMFIRE weather. |
| FOFEM | `Runtimes/Native/FOFEM/` | Kept, but nothing calls it yet. The DLL is a **Debug** build: it imports `VCRUNTIME140D.dll`, `ucrtbased.dll` and `FOFEMd`, which only machines with Visual Studio have. It needs a Release rebuild before anything uses it. |
| Open-Meteo | `Runtimes/Managed/Open-Meteo-DotNet/` | ERA5 archive and weather downloads. |
| SUMO C# glue (`Eclipse.Sumo.Libsumo`) | `Runtimes/Managed/Eclipse.Sumo.Libsumo/` | Generated by SWIG for the Windows SUMO 1.22 build. It does not match a Linux `libsumocs`; see [Linux](#linux). |
| k-PERIL | `PREACT/kPERILcore/` | Vendored source, built with the engine. |

ELMFIRE itself shells out to GDAL's **command-line tools** (`gdal_translate`, `gdalinfo`, `gdalsrsinfo`), which
are a separate dependency from the library above. Nothing in the repository provides them. They are looked
for on `PATH`, then in the newest `C:\Program Files\QGIS*\bin`, then `C:\OSGeo4W\bin` and `C:\OSGeo4W64\bin`,
then `%SUMO_HOME%\bin`; `[ELMFIRE] PathToGdal` or `--gdal` overrides the search.

## Building ELMFIRE

Every ELMFIRE run the platform starts sets `DUMP_MIDFLAME_WINDSPEED = .TRUE.`, which ELMFIRE only accepts from
commit **a7fb9d6** of the `ELMFIRE-WUINITY` branch on. An `elmfire.exe` built from an older commit (Nick's was
815debb) stops on that key, and the run fails with a message that ends
`this elmfire build predates DUMP_MIDFLAME_WINDSPEED; rebuild it from the ELMFIRE-WUINITY submodule (a7fb9d6 or later)`.

The platform finds ELMFIRE at `WUInity/Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe` (Windows) or
`.../build/linux/bin/elmfire` (Linux), searching upwards from the running program and from the working folder.
`[ELMFIRE] ElmfireExe` or `--elmfire` points elsewhere. It also takes ELMFIRE's default fuel model table from
`build/source/fuel_models.csv` beside that executable when a case has none.

### ELMFIRE on Windows

Prerequisites:

- the **Intel oneAPI Base and HPC Toolkits** (the `ifx` Fortran compiler and Intel MPI);
- the **"Desktop development with C++"** workload (MSVC and the Windows SDK), from the Visual Studio Installer
  or the standalone Build Tools. `ifx` compiles without it but links against the Microsoft C runtime, so the
  link fails with `lld-link` errors if it is missing.

Build from an ordinary `cmd` (the script sources oneAPI's `setvars.bat` itself when `ifx` is not on `PATH`):

```bat
cd WUInity\Assets\ThirdParty\elmfire\build\windows
make_windows.bat            :: Release
make_windows.bat debug      :: Debug (bounds checking, traceback)
make_windows.bat clean      :: removes obj\ and bin\
```

It writes `bin\elmfire.exe` and copies `impi.dll` beside it, so the executable runs outside a oneAPI shell —
including when WUInity starts it. It sets the hidden attribute on `bin\` and `obj\` so that Unity, which imports
everything under `Assets\`, skips the compiler output. `ONEAPI_ROOT` and `I_MPI_ROOT` override where oneAPI and
Intel MPI are looked for.

### ELMFIRE on Linux

With `gfortran` and an MPI Fortran wrapper (`mpifort`, from Open MPI or MPICH):

```sh
cd WUInity/Assets/ThirdParty/elmfire/build/linux
./make_gnu.sh elmfire       # the main executable only; without the argument it also builds the profiling/debug variants and elmfire_post
```

It installs `bin/elmfire_<version>` with a `bin/elmfire` link to it. `ELMFIRE_FCOMPL_MPI_GNU` and
`ELMFIRE_INSTALL_DIR` override the compiler and the install folder. The script rewrites the version string in
`build/source/elmfire.f90` from the `VERSION` file, which can leave the submodule showing a change.

The platform runs ELMFIRE as a single process on the namelist (no `mpiexec`), from the case or realization
folder, with the GDAL tools and a matching `PROJ_DATA` put first on its `PATH`.

## The Unity project

1. Run the build script (above).
2. Add `WUInity/` in Unity Hub and open it with **Unity 6000.3.15f1**.
3. Let NuGetForUnity restore its packages. `Assets/Packages` holds OsmSharp 6.2.0 and protobuf-net 2.3.7 — the
   same OsmSharp the engine is built against.
4. Open `Assets/WUInity/Scenes/WUInityMain.unity` and press **Play**.

The host is `Assets/WUInity/Core/WUInityManager.cs`, which implements the engine's `IExternalManager`. The GUI
is Dear ImGui (the UImGui package), under `Assets/WUInity/GUI/`. The window arrangement is kept in
`WUInity/imgui.ini`.

Keys are read from git-ignored files beside their templates:

| Key | File | Template |
|---|---|---|
| OpenTopography | `WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt` | `{"ApiKey":""}` |
| Mapbox | `WUInity/Assets/Resources/Mapbox/MapboxConfiguration.txt` | `{"AccessToken":"", ...}` |

The OpenTopography key can also come from the environment variable `OPENTOPOGRAPHY_API_KEY`, which wins over the
file, or be typed in for one session under Help > External tools and keys.

## Linux

The engine, `PREACT`, `PREACTcli`, the tests and ELMFIRE run on Linux; this is how v1 was tested end to end. The
Unity GUI is supported on Windows only. What Linux needs beyond the .NET 8 SDK:

| Need | Why | How |
|---|---|---|
| **GDAL 3.10** (`libgdal.so.36`) | The committed `libgdal_wrap.so` links against it. | A distribution package of 3.10, or a source build (e.g. into `/opt/gdal310`). Put its `lib` on `LD_LIBRARY_PATH`. |
| GDAL command-line tools | ELMFIRE's shell-outs. | Any recent version on `PATH`, or in `/usr/bin`, `/usr/local/bin` or `/opt/homebrew/bin`. |
| `PROJ_DATA` (and `GDAL_DATA`) | So GDAL finds a `proj.db` that matches it. | e.g. `/usr/share/proj`; the engine falls back to `/usr/share/proj` when neither `PROJ_DATA` nor `PROJ_LIB` is set. |
| SUMO 1.22 with the C# bindings | Traffic. | Built from source, with the Linux glue swapped in (below). `SUMO_HOME` pointing at it. |
| WindNinja | Terrain wind. | The probe on `PATH` looks for `WindNinja_cli.exe`, so on Linux set `WINDNINJA_CLI` to the executable, or pass `--windninja`. |
| ELMFIRE | Fires. | [ELMFIRE on Linux](#elmfire-on-linux) above. |

The engine registers its own native-library resolver on Linux (for the GDAL, NFDRS4 and FOFEM wrappers and
`libsumocs`), so `LD_LIBRARY_PATH` only needs GDAL itself. A typical environment:

```sh
export LD_LIBRARY_PATH=/opt/gdal310/lib
export PROJ_DATA=/usr/share/proj PROJ_LIB=/usr/share/proj
export GDAL_DATA=/opt/gdal310/share/gdal
export SUMO_HOME=$HOME/sumo-1.22
./build.sh
PREACT/PREACTexecute/bin/Release/net8.0/PREACT Examples/CFFDRS/Lytton/Lytton.wui
dotnet PREACT/PREACTcli/bin/Release/net8.0/PREACTcli.dll converge-trigger --wui ~/cases/mati/mati.wui --max 3 --hours 24 --parallel 1 --allow-uniform-weather
```

`PREACTcli` finds the `PREACT` apphost in `PREACT/PREACTexecute/bin/Release/net8.0/` by the platform's name, as
on Windows.

On the Linux test bench the NFDRS4 library did not load, so the live fuel moisture march and Nelson fell back to
uniform values, and there was no WindNinja. A campaign there needs `--allow-uniform-weather` (it otherwise
stops before its first fire); the results are then not terrain-resolved and should be treated as a pipeline test.

Each realization with SUMO needs several GB of memory; on a 7 GB machine two at once were killed by the
out-of-memory killer (exit 137, recorded as failed). Use `--parallel 1` on small machines.

### Regenerating the SUMO C# glue on Linux

The SWIG glue committed under `PREACT/PREACTcore/Runtimes/Managed/Eclipse.Sumo.Libsumo/` was generated for the
Windows SUMO build and must stay as it is. Against a Linux `libsumocs.so` every call fails with
`Unable to find an entry point named '?' in shared library 'libsumocs'`; since v1 a run whose cars mostly cannot
be put into SUMO stops with an error instead of evacuating nobody. To run traffic on Linux, **in a local copy
you do not commit**:

1. Get SUMO 1.22's source and apply two changes to it:
   - `src/libsumo/CMakeLists.txt`: in the `ENABLE_CS_BINDINGS` block, set
     `CMAKE_SWIG_FLAGS` to `-namespace Eclipse.Sumo.Libsumo` (the namespace the engine uses);
   - `src/libsumo/libsumo_typemap.i`: add an empty `SELF_NULL_CHECKER(traci_class)` macro in the `#else`
     branch before its `#endif`, since the macro is otherwise undefined for C#.
2. Build it with `cmake -DENABLE_CS_BINDINGS=ON` (SWIG 4.x; 4.2.0 was used) and `make`.
3. Copy `build/cmake-build/src/libsumo/cs/*.cs` over `PREACT/PREACTcore/Runtimes/Managed/Eclipse.Sumo.Libsumo/`,
   and delete the classes in that folder the Linux build did not generate (nine of them for 1.22).
4. Set `SUMO_HOME` to the build; its `bin/` holds `libsumocs.so`.
5. Rebuild `PREACTexecute` (or run `build.sh`).
