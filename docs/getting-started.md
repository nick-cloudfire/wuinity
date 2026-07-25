# Getting started

This guide takes you from a fresh clone to a completed simulation, using the
bundled **Roxborough** example (Colorado, USA). It is the most complete,
up-to-date example and matches the current engine.

## 1. Prerequisites

- **Windows** (the `dev` branch is Windows-only — see [Building](building.md)).
- **.NET 8 SDK** — <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>.
- **SUMO 1.22**, installed with all extras — <https://eclipse.dev/sumo/>. Needed
  for traffic and for the bundled GDAL. See [Troubleshooting](troubleshooting.md)
  if GeoTIFF/traffic fails to load.
- **Unity** — only if you want the visualizer. Add the project in Unity Hub and
  it will tell you the exact editor version.

You do **not** need a Mapbox token to run a simulation; it is only used for the
map background in the visualizer.

## 2. Build the engine and the command-line runner

```sh
dotnet build PREACT/PREACTexecute/PREACTexecute.csproj -c Release
```

This builds `PREACTcore` (the engine) and `PREACTexecute` (the head-less
runner). The runner executable is
`PREACT/PREACTexecute/bin/Release/net8.0/PREACT.exe`.

> Building `PREACTcore` in **Release** also copies the engine DLLs into the
> Unity project at `WUInity/Assets/PREACT/`. Always build Release before opening
> WUI-NITY in Unity.

## 3. Run a simulation (command line)

```sh
PREACT/PREACTexecute/bin/Release/net8.0/PREACT.exe \
    Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui
```

The run logs progress to the console. When it finishes, results appear in
`Examples/NFDRS4_Behave/Roxborough/_output/`. See [Output files](output-files.md)
for what each file contains.

To run a Monte-Carlo batch (the run stops early once results converge):

```sh
# PREACT.exe <file.wui> <numberOfRuns> <batchSize> <offset>
PREACT.exe Roxborough_no_smoke.wui 20 4 0
```

See [Command-line tools](command-line-tools.md) for the full argument reference.

## 4. Run in the visualizer (Unity)

1. Build `PREACTcore` in Release (step 2) so the DLLs are up to date in
   `WUInity/Assets/PREACT/`.
2. Open the `WUInity/` project in Unity (the version Unity Hub reports).
3. Open the scene `WUInity/Assets/WUInity/Scenes/WUInityMain.unity`.
4. (Optional) add a Mapbox token — see [Troubleshooting](troubleshooting.md).
5. Press **Play**, then load a `.wui` project from the in-app menu.

## 5. Prepare your own scenario

The [QGIS plugin](qgis-plugin.md) is the easiest way to build the input data
(domain, destinations, evacuation groups, population, road network) and export a
ready-to-run `.wui`. To understand or hand-edit that file, read
[The `.wui` input file format](input-file-format.md).

## Where to go next

- [The `.wui` input file format](input-file-format.md)
- [Examples](examples.md) — what else ships in `Examples/`
- [Module status](modules.md) — what is production-ready vs experimental
- [Troubleshooting](troubleshooting.md)
