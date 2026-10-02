# WUInity / PREACT

WUInity/PREACT (also written WUI-NITY) is licensed under the GNU General Public License v3.0.
Included third-party source code carries its own licenses.

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

## What it is

A platform for wildfire evacuation at the wildland–urban interface. For a place you choose — anywhere on
earth, with the continental US best supported — it builds a fire case, runs a fire, evacuates the population
through the road network, and computes an **evacuation trigger boundary**: the line a fire must not cross
before the community is ordered out. A **trigger campaign** repeats that over many fires drawn from the
area's fire-weather history until the probability of each cell lying inside the boundary stops changing.

| Piece | What it does |
|---|---|
| **PREACT** (`PREACT/PREACTcore`) | The simulation engine (C#, netstandard2.1): scenario input, time stepping, fire, evacuation, smoke, trigger boundary. |
| **ELMFIRE** (submodule) | The fire model, run as an external Fortran program. PREACT builds its case, runs it and reads its rasters back. |
| **Household model** (`MacroHouseholdSim`) | Households respond on a departure-time curve, or earlier when the fire front comes near their home, walk to their car and drive. |
| **SUMO** | Traffic simulation of the cars, through libsumo. |
| **k-PERIL** (`PREACT/kPERILcore`) | Trigger boundaries: back-propagates the fire's spread from the WUI area for the evacuation's required time. |
| **WUInity** (`WUInity/`) | The Unity visualizer and GUI: a thirteen-step workflow panel from an empty folder to a trigger campaign. |
| **PREACT.exe** (`PREACT/PREACTexecute`) | Runs a scenario without Unity. |
| **PREACTcli** (`PREACT/PREACTcli`) | Builds ELMFIRE cases (`build-case`), runs trigger campaigns (`converge-trigger`), makes population files. |

Simulations run in UTM coordinates. A scenario is a plain-text `.wui` file; everything it refers to lives in
its folder.

## Requirements

| What | Needed for | Notes |
|---|---|---|
| **Windows (x64)** | everything | The GUI and the full pipeline are supported on Windows. The engine and CLI also run head-less on Linux — see [Building](docs/building.md#linux). |
| **.NET 8 SDK** | building | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| **Unity 6000.3.15f1** | the GUI | The version in `WUInity/ProjectSettings/ProjectVersion.txt`. |
| **SUMO 1.22**, installed with extras | traffic, and the engine's native GDAL | <https://eclipse.dev/sumo/>. `SUMO_HOME` set, its `bin` on `PATH`, or its folder set under Help > External tools and keys. |
| **GDAL command-line tools** | ELMFIRE | `gdal_translate`, `gdalinfo`, `gdalsrsinfo`. A QGIS or OSGeo4W install provides them; they are found automatically, or set their folder under Help > External tools and keys. |
| **ELMFIRE, built from the submodule at a7fb9d6** | every fire | That commit adds `DUMP_MIDFLAME_WINDSPEED`, which every run now asks for. An older `elmfire.exe` refuses every run. See [Building ELMFIRE](docs/building.md#building-elmfire). |
| **WindNinja** (`WindNinja_cli`) | terrain-resolved wind | Without it the wind is one value over the whole domain; a campaign refuses to start unless told to accept that. |
| **OpenTopography API key** | the fire case's DEM | Free from <https://opentopography.org>. |
| **Mapbox access token** | the map background | Not needed to run anything. |
| Internet access | data steps | OpenStreetMap, WorldPop, Open-Meteo (ERA5), LANDFIRE (US), OpenTopography. |

## Quick start

```powershell
git clone --recurse-submodules <repository url>
cd <the cloned folder>

# 1. Build the engine for Unity, PREACT.exe and PREACTcli.exe. Run it again after every pull that touches PREACT/.
powershell -ExecutionPolicy Bypass -File .\build.ps1

# 2. Build ELMFIRE once (Intel oneAPI HPC Toolkit + the MSVC "Desktop development with C++" workload), from a plain cmd:
WUInity\Assets\ThirdParty\elmfire\build\windows\make_windows.bat
```

3. Put your keys in place: copy `WUInity/Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt`
   to `OpenTopographyConfiguration.txt` beside it and fill in `ApiKey`; do the same for the Mapbox
   `MapboxConfigurationTemplate.txt` (`AccessToken`). Both files are git-ignored.
4. Open `WUInity/` in Unity 6000.3.15f1, open the scene `Assets/WUInity/Scenes/WUInityMain.unity` and press
   **Play**. The **Scenario workflow** panel on the left takes a new scenario (File > New scenario) through
   thirteen steps to a trigger campaign. [Getting started](docs/getting-started.md) walks through them.

Head-less, the shipped example needs only SUMO — no ELMFIRE, Unity or key (its fire is imported from
FlamMap rasters):

```powershell
PREACT\PREACTexecute\bin\Release\net8.0\PREACT.exe Examples\NFDRS4_Behave\Roxborough\Roxborough_no_smoke.wui
```

Results go to `Examples\NFDRS4_Behave\Roxborough\_output\`. A campaign from the command line:

```powershell
PREACT\PREACTcli\bin\Release\net8.0\PREACTcli.exe converge-trigger --wui D:\cases\mati\mati.wui --max 200
```

WUInity as a program that runs without Unity, `dist\WUInity\WUInity.exe` with PREACT and ELMFIRE beside it
(close the Unity editor first; see [Distribution](docs/distribution.md)):

```powershell
powershell -ExecutionPolicy Bypass -File .\build-player.ps1
```

## Documentation

- [Getting started](docs/getting-started.md) — the GUI's thirteen workflow steps, end to end.
- [Building](docs/building.md) — the build scripts, what goes where, ELMFIRE, Linux.
- [Distribution](docs/distribution.md) — the standalone program (`build-player.ps1`): what ships, what to install, where the tools and keys are found.
- [The `.wui` input file format](docs/input-file-format.md) — every section and key.
- [ELMFIRE cases](docs/elmfire-cases.md) — how a fire case is built and run.
- [Trigger campaigns](docs/trigger-campaigns.md) — the probabilistic trigger boundary.
- [Command-line tools](docs/command-line-tools.md) — `PREACT.exe` and `PREACTcli`.
- [Output files](docs/output-files.md) — what a run and a campaign write.
- [Modules](docs/modules.md) — what each module does and how far it is validated.
- [Examples](docs/examples.md) — what ships in `Examples/`.
- [Troubleshooting](docs/troubleshooting.md) — the messages you will meet and what to do.
- [Manual test of the v1 GUI](docs/manual-test-v1.md) — a scripted check of the GUI on Windows.
- [Verification](docs/verification.md) — the basic verification cases (`verify.ps1` / `verify.sh`), their results and
  the discrepancies they found.
- [Changelog](CHANGELOG.md) — what v1 changed, and which results differ from earlier runs.

## Development

WUInity/PREACT is publicly available and we welcome issues, bug reports, suggestions and pull requests.
Please keep in mind that nobody develops the software full-time.
