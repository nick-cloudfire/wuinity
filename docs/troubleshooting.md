# Troubleshooting

## Windows only

The `dev` branch links native x64 libraries (GDAL, FOFEM, NFDRS4) and
SUMO, so it currently runs on Windows only. There is no supported Linux/macOS
build yet.

## GeoTIFF or traffic fails to load / GDAL errors

The engine loads native GDAL and SUMO DLLs from your **SUMO** installation at
run time. Check that:

- **SUMO 1.22** is installed (the pinned version — see
  `PREACT/PREACTcore/Source/Evacuation/Traffic/Modules/SUMO/version_info.txt`),
  **with all extras** (that package includes GDAL).
- The SUMO `bin` directory is on `PATH`.
- Your logged-in account can read the SUMO install directory. Running as a local
  administrator avoids permission problems.

## Unity reports missing PREACT types, or shows old behaviour after an engine change

The engine DLLs are not committed. Run `build.ps1` (Windows) or `build.sh` (Linux)
before opening the Unity project, after cloning and after pulling engine changes.
It builds PREACTcore in Release, the only configuration that writes into
`WUInity/Assets/PREACT/Release/`; a Debug build writes to `PREACT/PREACTcore/bin/`
and Unity keeps using whatever DLLs it had. See [Building](building.md).

## The map background is missing in the visualizer

A [Mapbox](https://www.mapbox.com/) access token is required for the map
background (but not to run a simulation). Put your token in
`WUInity/Assets/Resources/Mapbox/MapboxConfiguration.txt` (copy
`MapboxConfigurationTemplate.txt` as a starting point).

## The command-line runner does nothing / crashes on start

- Pass the `.wui` path as the first argument. With no arguments the tool prints
  usage and exits.
- For batch runs pass **all four** arguments:
  `PREACT.exe <file.wui> <numberOfRuns> <batchSize> <offset>`.
- If the file path contains spaces, quote it — and note that **values inside the
  `.wui` file may not contain spaces** (all spaces are stripped when parsing).

## An example won't load

Some older examples reference [modules or keys](modules.md) that are broken or no
longer read by the current engine (e.g. `MacroTrafficSim`, or a fire module
value the parser rejects). Start from
[`Examples/NFDRS4_Behave/Roxborough`](examples.md) and validate your file
against [the input format reference](input-file-format.md).

## A `.wui` key seems to be ignored

Check the [legacy keys list](input-file-format.md#legacy-keys). Several keys that
appear in older examples (`EvacuationOrderStart`, `UTMoffset`, `MaxSimTime`,
`RootFolder`, `WeatherStreamFile`, `GraphicalFireInputFile`) are silently
ignored by the current parser.

