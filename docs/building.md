# Building from source

## Repository layout

| Path | Contents |
|------|----------|
| `PREACT/PREACTcore/` | The simulation engine (C# library, `netstandard2.1`). |
| `PREACT/PREACTexecute/` | Head-less simulation runner (console app, `net8.0`, produces `PREACT.exe`). |
| `PREACT/PREACTcli/` | Utility CLI (`net8.0`) — currently generates population CSVs. |
| `PREACT/FeatureTester/` | Internal test harness. |
| `WUInity/` | Unity visualizer project. |
| `Examples/` | Ready-to-run example scenarios. |
| `docs/` | This documentation. |

## Prerequisites

- **Windows.** The `dev` branch links native x64 libraries (GDAL, FOFEM,
  NFDRS4) and SUMO, so it currently runs on Windows only.
- **.NET 8 SDK** for the engine and CLI tools.
- **Unity** for the visualizer (version reported by Unity Hub when you add the
  `WUInity/` project).
- **SUMO 1.22** installed with all extras — required at run time for traffic and
  for the bundled GDAL DLLs. The exact version is pinned in
  `PREACT/PREACTcore/Source/Evacuation/Traffic/Modules/SUMO/version_info.txt`.

## Building everything: the build script

```sh
powershell -ExecutionPolicy Bypass -File build.ps1   # Windows (PowerShell 5.1 or 7)
./build.sh                                           # Linux
```

Both build `PREACTcore`, `PREACT.exe` and `PREACTcli.exe` in Release, check that
every engine file the Unity project expects was produced (each has a committed
`.meta` in `WUInity/Assets/PREACT/Release/`), print where the two executables
are, and stop with `BUILD FAILED` and a non-zero exit code on any error.

The engine DLLs in `WUInity/Assets/PREACT/Release/` are build output and are not
committed; only their `.meta` files are, so Unity keeps the same GUIDs and import
settings. After cloning, and after pulling engine changes, run the script before
opening the Unity project. If it reports a file in use (MSB3021/MSB3027), close
the Unity editor, which keeps the native plugins loaded, and run it again.

## Building the projects one by one

```sh
# Engine only
dotnet build PREACT/PREACTcore/PREACTcore.csproj -c Release

# Engine + head-less runner (PREACT.exe)
dotnet build PREACT/PREACTexecute/PREACTexecute.csproj -c Release

# Engine + population utility (PREACTcli.exe)
dotnet build PREACT/PREACTcli/PREACTcli.csproj -c Release
```

### Output routing (important)

`PREACTcore.csproj` sends its build output to different places depending on the
configuration:

| Configuration | Output goes to |
|---------------|----------------|
| **Release** | `WUInity/Assets/PREACT/Release/netstandard2.1/` (so Unity picks up the fresh engine DLLs). |
| **Debug** | `PREACT/PREACTcore/bin/`. |

So: **build PREACTcore in Release before opening the Unity project**, otherwise
the visualizer will use stale engine DLLs. A Debug build will not update Unity.

### Bundled native/managed dependencies

Third-party libraries live under `PREACT/PREACTcore/Runtimes/` (and
`ThirdParty/`) and are copied to the output as needed:

- **GDAL / OGR / OSR** – the C# wrappers ship in the repo; the underlying native
  GDAL comes from your **SUMO** install at run time. This is why the correct
  SUMO version must be installed and readable on `PATH`.
- **FOFEM, NFDRS4** – native x64 DLLs, committed under `Runtimes/Native/`.
  Nothing in the engine calls FOFEM yet, and its DLL is a Debug build: it
  imports the debug C runtime (`VCRUNTIME140D.dll`, `ucrtbased.dll`), which only
  machines with Visual Studio have, so it needs a Release rebuild before use.
- **Open-Meteo** – a managed DLL, committed under `Runtimes/Managed/`.
- **k-PERIL** (`kPERILcore.dll`) – built from the vendored source in
  `PREACT/kPERILcore/`, so a clean checkout builds without any sibling
  repositories.

## Building the Unity visualizer

1. Run the build script (above), or build `PREACTcore` in **Release**.
2. Open `WUInity/` in Unity Hub with the reported editor version.
3. Open the scene `WUInity/Assets/WUInity/Scenes/WUInityMain.unity`.
4. Press **Play**.

The Unity host is `WUInity/Assets/WUInity/Core/WUInityManager.cs`, which
implements the engine's `IExternalManager` interface. The runtime GUI is the
Dear ImGui-based UI under `WUInity/Assets/WUInity/GUI/DearIMGUI/`.
