# PREACT / WUI-NITY

PREACT/WUI-NITY is licensed under the GNU General Public License v3.0. Included
third-party source code carries its own licenses.

> ### Important notice and disclaimer
> By accessing, downloading and using the PREACT/WUI-NITY Modelling Platform Tool,
> you expressly agree to the following.
>
> The tool simulates fire behaviour and human/traffic movement during a wildfire
> evacuation at the wildland–urban interface (WUI). It is intended to enhance the
> situational awareness of Authorities Having Jurisdiction ("AHJs") as they plan
> and train for potential WUI fire scenarios. **It is not designed to replace or
> substitute an AHJ's decision about evacuation during a wildfire.**
>
> Use of this tool is at the user's own risk. It is provided AS IS and AS
> AVAILABLE, without guarantee or warranty of any kind, express or implied
> (including the warranties of merchantability and fitness for a particular
> purpose) and without representation or warranty regarding its accuracy,
> completeness, usefulness, timeliness, reliability or appropriateness. The
> creators assume no responsibility or liability in connection with the
> information or opinions contained in or expressed by this tool, its use or its
> output.

This is software under active development. Incomplete features and bugs are
present. Please report any issues on GitHub.

---

## What is this?

WUI-NITY (also written WUInity / WUI-nity) began as a platform combining
pedestrian and traffic evacuation simulation with wildfire-spread simulation,
built in the Unity game engine. As the software matured it was decoupled from
Unity (to enable head-less runs on HPC, and to avoid Unity licensing
constraints) and generalised beyond wildfire. That simulation engine is
**PREACT**; **WUI-NITY** now survives as a Unity-based *visualizer* on top of it.

PREACT can run without Unity through the **command-line tools**, which also allow
many simulations to run at once.

### Architecture at a glance

| Piece | Role |
|-------|------|
| **PREACTcore** | The simulation engine (C# library, `netstandard2.1`). |
| **`Engine`** | Entry point of PREACTcore. Needs an `IExternalManager` host. |
| **`IExternalManager`** | Implemented by the host: either WUI-NITY (Unity) or the CLI. Receives messages and collects data from a running simulation for visualization. |
| **`EngineTask`** | Describes a run (serial / batch); the engine manages one or many simulations. |
| **WUI-NITY** | Unity project that hosts the engine and visualizes it. |
| **PREACTexecute / PREACTcli** | Console tools that host the engine head-less. |
| **QGIS plugin** | Prepares input data (population, road network, domain, destinations, evacuation groups) and exports a `.wui` project. |

Simulations run in **UTM coordinate space**, which matters for the traffic and
fire-spread data. A [Mapbox](https://www.mapbox.com/) access token is needed for
the map background in the visualizer, but is **not** required to run a
simulation.

---

## Documentation

Full end-user documentation lives in [`docs/`](docs/):

- **[Getting started](docs/getting-started.md)** – install prerequisites, build, and run your first simulation.
- **[Building from source](docs/building.md)** – detailed build steps, output routing, and external dependencies.
- **[The `.wui` input file format](docs/input-file-format.md)** – complete reference for every section and key.
- **[Command-line tools](docs/command-line-tools.md)** – `PREACTexecute` (run simulations) and `PREACTcli` (generate population).
- **[Examples](docs/examples.md)** – what ships in `Examples/` and which to start with.
- **[Module status](docs/modules.md)** – which fire / traffic / pedestrian / smoke / trigger modules are production-ready vs experimental.
- **[Output files](docs/output-files.md)** – what a run produces and what each file contains.
- **[QGIS plugin](docs/qgis-plugin.md)** – preparing input data with the plugin.
- **[Troubleshooting](docs/troubleshooting.md)** – common problems and fixes.

---

## Requirements

- **Windows.** The `dev` branch is Windows-only because of the native libraries
  (GDAL, Behave, FOFEM, NFDRS4, SUMO) it links against.
- **.NET 8 SDK** to build the engine and CLI tools
  ([download](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)).
- **Unity** – only if you want the visualizer. Add the cloned project in Unity
  Hub; it will report the exact editor version required.
- **SUMO 1.22** for traffic simulation, installed **with all extras** (that is
  where the bundled GDAL comes from). See [Module status](docs/modules.md) and
  [Troubleshooting](docs/troubleshooting.md). Download:
  <https://eclipse.dev/sumo/>.

The logged-in account must be able to read the SUMO install directory on `PATH`
(the linked SUMO DLLs are loaded from there). Running as a local administrator
avoids permission issues.

---

## Quick start

```sh
# 1. Build the engine + command-line runner (.NET 8 SDK required)
dotnet build PREACT/PREACTexecute/PREACTexecute.csproj -c Release

# 2. Run the reference example head-less
PREACT/PREACTexecute/bin/Release/net8.0/PREACT.exe \
    Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui
```

Outputs are written to an `_output/` folder next to the `.wui` file. For the
Unity visualizer and a full walk-through, see
[Getting started](docs/getting-started.md).

> Building the `Release` configuration of **PREACTcore** copies the engine DLLs
> into the Unity project (`WUInity/Assets/PREACT/`). Build PREACTcore in
> `Release` before opening WUI-NITY in Unity. See [Building](docs/building.md).

---

## Development

WUI-NITY is publicly available and we welcome issues, bug reports, suggestions
and pull requests. Please keep in mind that nobody develops the software
full-time.
