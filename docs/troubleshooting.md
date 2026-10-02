# Troubleshooting

The messages below are quoted as the platform prints them, with `…` for the parts that vary. Most appear in the
visualizer's console (View > Console), in the workflow row of the step concerned, and in the run's log
(`_output/<Name>.log`). Help > External tools and keys shows what was found on this machine and where it was
looked for; start there when something external is missing.

## Building

| Message | Fix |
|---|---|
| `BUILD FAILED: dotnet was not found on PATH. …` / `BUILD FAILED: no .NET SDK 8 or newer found …` | Install the .NET 8 SDK and open a new terminal. |
| `BUILD FAILED: PREACT/PREACTcore/PREACTcore.csproj did not build (see the errors above). …`, with `MSB3021` or `MSB3027` (a file in use) | Close the Unity editor — it keeps the native GDAL and NFDRS4 plugins loaded — and run the script again. |
| `BUILD FAILED: the Unity engine folder is incomplete: the files above have a .meta but were not built.` | The engine's output set changed without its `.meta` files. If you removed an output on purpose, delete its `.meta` too. |
| Unity: `CS0246: The type or namespace name 'PREACT' could not be found`, or old behaviour after a pull | The engine DLLs are not committed. Close Unity, run `build.ps1`, open Unity again. A Debug build of `PREACTcore` does not update the Unity folder; only the Release build the script makes does. |
| ELMFIRE's `make_windows.bat` fails to link with `lld-link` errors | Install the "Desktop development with C++" workload (MSVC and the Windows SDK). |

Details: [Building](building.md).

## Keys and the map

| Symptom or message | Fix |
|---|---|
| The map is blank; External tools says `Mapbox token: not valid. The map tiles cannot be loaded.` | Put a Mapbox token in `WUInity/Assets/Resources/Mapbox/MapboxConfiguration.txt`. Only the map background needs it. |
| `No OpenTopography API key.` (step 5, which it blocks) | Copy `OpenTopographyConfigurationTemplate.txt` to `OpenTopographyConfiguration.txt` in `WUInity/Assets/Resources/OpenTopography/` and paste your key in, or set `OPENTOPOGRAPHY_API_KEY`. Help > External tools and keys takes one for the current session. |
| `… so one has to be downloaded - but no OpenTopography key was found, and no local DEM covering the padded domain was given. …` (case build, `build-case`) | The same key; or pass a DEM covering the coordinates it prints with `build-case --dem`. |
| `OpenTopography DEM download failed after N attempts.` | The cause is in the lines before it (`DEM download failed: …`). A `401` or `403` status means the key was refused; anything else is the service or the network, so try again later. |

## Opening a scenario

| Message | Fix |
|---|---|
| `Could not interpret user input <value> for <key>.` | The value does not parse for that key (a comma for a decimal point, a misspelled module or unit name). The scenario check lists it as critical: fix it, see [the input format](input-file-format.md). |
| `Could not interpret user input <value> for <key>; its default is used.` | Not critical: the default applies. Fix it if the default is not what you meant. |
| `<key> was not found, this value is critical for the simulation to function …` | A required key is missing. |
| `[WildfireModule] Enabled: Not set, so the fire module is off and the scenario runs without a fire. …` (and the same for the household, traffic, smoke and trigger-boundary modules) | The section has no `Enabled` line, so the module is off. Add `Enabled=true` to run it, or `Enabled=false` to say it is meant to be off. The workflow panel shows it as a warning. |
| `<section> gives <key> more than once; the first value (…) is used and the one on line N (…) is ignored.` | Delete one of them. A GUI save keeps the first. |
| `[<section>] is ignored. … It will not be written when the scenario is saved.` | A retired section; see [Retired keys and sections](input-file-format.md#retired-keys-and-sections). |
| `Refers to "<name>", which does not exist.` | A group names a destination, response curve or demographics that no section defines. Names are case-sensitive. |
| `[<section>] <key> (line N) is not a key of [<section>] - did you mean <Key>? It is ignored, and saving the scenario does not write it.` | A misspelt key (keys are case-sensitive) or one from another section. Correct it by hand; a GUI save drops it. The same message names an unknown `[section]`, and a line that is not `Key=Value`. |
| `[<section>] <key> (line N) is no longer used: … It is ignored, and saving the scenario does not write it.` | A retired key; see [Retired keys and sections](input-file-format.md#retired-keys-and-sections). Delete it, or let the next save drop it. |
| `PREACT.exe`: `The scenario did not load as runnable; see the items listed above.` (exit 1) | Fix the critical items listed above it. The GUI's Scenario > Check scenario gives the same list, filed under the workflow's steps. |
| A file named in the scenario is found in another folder | A file that has moved is looked for by name nearby; the scenario check says which copy it used, and a save records the new path. |

## Preparing data (workflow steps 2 to 5)

| Message | Fix |
|---|---|
| `SUMO was not found (SUMO_HOME, or a folder on PATH); building the network needs its netconvert.` / `netconvert could not be found. …` | Install SUMO 1.22 and set `SUMO_HOME`, or put its `bin` on `PATH`; then Help > External tools and keys > Look again. |
| `WorldPop has no <country> data for <year>; using <year> instead (the closest available). …` | Information only. |
| `Outside the US there is no fuel download here: name a fuel model raster of your own, …` | LANDFIRE covers the US only. Name your own fuel raster under Fuels, canopy and buildings > Source layers. |
| `The LANDFIRE (LFPS) job <id> failed: …` / `… did not finish within N minutes.` | LANDFIRE's service; try again later. |
| `LANDFIRE asks every download for a contact e-mail, and none is set. …` | Enter yours under Fuels, canopy and buildings > Get them (kept for your user), or set `LANDFIRE_EMAIL`. |
| `Downloading the OSM roads failed: no Overpass server delivered the data after N attempts. … What each said: …` | The public Overpass servers are busy (504, 429); the step tried each in turn, three times. Try again later, or name another server in `PREACT_OVERPASS_URLS` (`;`-separated interpreter URLs). Nothing was written, so the RouterDb and SUMO steps did not run on stale data. |
| `Area of interest: That was a double click on corner 1 …` / `Corner 2 cannot be where corner 1 is …` | The pick needs two opposite corners, one click each; it is still waiting for the second. |
| `No canopy: it is filled with zeros, so the fire is surface fire only - no crown fire.` | Add canopy rasters (LANDFIRE in the US), or accept a surface fire. |
| `No WindNinja: the case gets one wind value for the whole domain, so a trigger boundary comes out circular.` | Install WindNinja, or set `WINDNINJA_CLI` to the executable (`WindNinja_cli.exe`, `WindNinja_cli` on Linux), or `[ELMFIRE] WindNinjaExe`. Then rebuild the case's weather. |
| `No GDAL command-line tools were found. ELMFIRE shells out to them, and fails its own DEM check without them.` | Install QGIS or OSGeo4W (found by themselves), or set `[ELMFIRE] PathToGdal` to a folder holding `gdal_translate`. |
| `The ignition area was painted on a W x H grid (…); the fire grid … is …` (step 6) | The painting belongs to another grid. Use **Move painting onto the fire-case grid** in step 6; it writes a new file. |
| `The painted areas in <file> are W x H cells, but the fire-case grid is … so there is no telling which ground they were painted on. …` (a build) | As above: move the painting in step 6, or repaint on the case grid. The same refusal, with how the grids differ (`starts 300 m west of the grid the painting was made on`), is given for a painting of the right size whose record puts it elsewhere. |
| `… STOPPED: The case build was stopped while its weather was being made: no wind was written …` | You pressed Stop (or quit) during the build. Nothing is half-written: no wind, not even a uniform field, and the scenario is unchanged. Build again; it carries on from what the stopped build kept. |
| `The painted ignition area is newer than the case's ignition_mask.tif: apply it to the case.` | Step 6 > **Apply to case**. |
| `… holds a painted WUI area (N cells), which is no longer used …` / `… holds a painted initial ignition (N cells) …` | An older painting. The WUI area is the evacuation groups (step 9); the initial ignition became an ignition point (save the scenario to keep it). Saving the painted areas again drops both. |
| `Group <name>: its mask is W x H, not on the fire grid (…). It is used where it lies, …` (step 9) | The group was painted on an earlier grid (the case was re-cut since). Nothing is wrong with its households or the WUI area, which read the mask where it lies; only painting cannot continue from it. **Repaint** it on the fire grid if you want to edit it. |
| `a trigger campaign is running (…) and every one of its realizations reads this case's rasters, …` | A case build is refused while a campaign of this scenario runs. Wait for it, or cancel it. |

## Running a fire (ELMFIRE)

A run that cannot compute its fire stops with `ELMFIRE did not produce a fire: <reason>`. The reasons:

| Reason | Fix |
|---|---|
| `ELMFIRE cannot run: [ELMFIRE] ElmfireExe is not set and there is no ThirdParty/elmfire/build/windows/bin/elmfire.exe above …` | Build ELMFIRE with `make_windows.bat` ([Building ELMFIRE](building.md#building-elmfire)), or set `ElmfireExe`. |
| `… this elmfire build predates DUMP_MIDFLAME_WINDSPEED; rebuild it from the ELMFIRE-WUINITY submodule (a7fb9d6 or later) …` | Rebuild ELMFIRE from the submodule. |
| `There is no ELMFIRE case at <folder>. Turn BuildCase on, or build one …` | Build the case (step 5, or `PREACTcli build-case`). |
| `The namelist … names rasters that are not on the case grid, so ELMFIRE cannot run it: …` | Build the case again: it re-cuts every raster the namelists name onto the grid. |
| `The case's weather cannot run this fire: the weather covers 24 h (24 bands) and the fire runs 30 h. The scenario runs its own NamelistTemplate …` | The case's weather is shorter than the fire the template (a kept `elmfire.data.kept-<time>`, say) runs. Lower `[ELMFIRE] SimulationTstopHours`, or build the case again for the hours you need. A generated case extends its own weather. The band keys a template brings (`NUM_METEOROLOGY_TIMES = 72`) are not the problem: a run sets them from the case's `ws.tif` and says so (`Namelist weather bands fitted to ws.tif ...`). |
| `The namelist … cannot run: it switches the building spread model on (&WUI USE_BLDG_SPREAD_MODEL) and ELMFIRE would read …/building_fuel_models.csv, which is not there …` | Put the table there, or switch the building spread model off. With ELMFIRE's source tree beside the executable, its own table is copied in without asking. |
| `elmfire burned 0 acres (the ignition most likely landed on non-burnable fuel)` | Move the ignition onto burnable fuel. |
| `ELMFIRE hit its wall-clock limit (MAX_RUNTIME) and stopped the fire early: …` | Raise `MAX_RUNTIME` (seconds) in Fire behaviour; its default, 999999, never stops a single run. |
| `elmfire exited N: …` with ELMFIRE's own first lines | Some of ELMFIRE's messages point at the wrong thing (`DEM CRS does not appear to use metre linear units` means the GDAL tools were not found, for example): see [ELMFIRE errors that name the wrong thing](elmfire-cases.md#elmfire-errors-that-name-the-wrong-thing). ELMFIRE's full output is in the case's `elmfire.log`. |

## Running the evacuation

| Message | Fix |
|---|---|
| `Could not start SUMO, aborting. …` mentioning `libsumocs` | SUMO's `bin` is not found: set `SUMO_HOME` or add it to `PATH`. On Linux, see [the SUMO glue](building.md#regenerating-the-sumo-c-glue-on-linux). |
| `Could not start SUMO. …` | `[SUMO] ConfigurationFile` does not lead to a `.sumocfg`; the rest of the message says what it found. Build the network in step 2, or correct the path. |
| `The SUMO network is projected in EPSG:A but the simulation measures in EPSG:B. …` | The network was built for another area or zone. Rebuild it from the scenario (step 2, **Rebuild everything**). |
| `N of the first M cars could not be put into SUMO (…), so the evacuation would run without them. Stopping the run.` | If it adds `The C# bindings … do not match this SUMO's libsumocs`, use the SUMO 1.22 the bindings were made for (Windows) or regenerate them (Linux). If it says most had no route, the network does not cover the population and the destinations. |
| `Car could not be injected as no valid route was found or cached. …` | A household's road access point or its destination is not connected to the network. Snap the destination to a road lane (step 7), or regenerate the population on the current network (step 3). |
| `Households that left because of the fire's proximity: N (M people)` | Information: fire reaction is on by default in v1 (`[MacroHouseholdSim] ReactToFire`). |

## The trigger boundary

| Message | Fix |
|---|---|
| `The fire never reached <area>, so no trigger boundary was computed for it. …` | Not an error: the fire did not reach the WUI area within its duration. Lengthen the fire or check the ignition. |
| `No evacuation arrivals were recorded, so the required egress time is zero and the trigger boundary would collapse onto the WUI area. Not computing one. …` (exit 2) | The traffic did not run, or no car reached a destination. See the evacuation messages above. |
| `Can't compute a trigger boundary without a wildfire module: …` | Enable the fire, or turn the boundary off. |
| `k-PERIL has no topography, so the boundary is computed as if the ground were flat.` | Name slope and aspect under `[Landscape]` (an ELMFIRE case build does this). An ELMFIRE run uses its case's own `dem/slp/asp.tif` (`k-PERIL topography: the fire's own terrain ...`). |
| `0_trigger_boundary.asc was written without a .prj: GDAL could not describe EPSG:… . GIS will not know its CRS …` | GDAL's PROJ database was not found (set `PROJ_DATA` or `PROJ_LIB` to PROJ's `share/proj`). Not for a UTM grid, whose `.prj` is written without it. The Results window marks such a boundary *(earlier version)*; it is not one. |
| The boundary is round | The fire ran under one wind for the whole domain: no WindNinja when the case was built. |

## Trigger campaigns

A campaign checks its prerequisites before the first fire and stops with the reason (exit 1). The checks, and what
each needs, are in [Trigger campaigns](trigger-campaigns.md#before-you-start). Those most often met:

| Message | Fix |
|---|---|
| `WindNinja was not found (…). Without it every realization's wind is one value across the whole domain, …` | Install WindNinja or pass `--windninja <exe>`; or tick *Allow uniform weather* (`--allow-uniform-weather`) for a pipeline test. |
| `… is reached from a realization as '…', which contains a space. …` | Move or rename the scenario folder so the path has no spaces. ELMFIRE passes paths to GDAL unquoted. |
| `every realization protects the WUI area, and …wui_area.tif is not there / has no marked cell.` | Paint the evacuation groups (step 9) and apply them to the case (it writes `wui_area.tif` from them). |
| `the case's wui_area.tif (N cells) is not the WUI area of the scenario's evacuation groups …` | The groups changed since the case was built, or an older build wrote it from a painted WUI area: **Apply to case** (or `build-case`), then start the campaign. |
| `the archive holds no day with a non-zero fire weather index, …` | The ERA5 archive is empty or wrong for the place; delete the case's `climatology/` CSV so the campaign fetches it again. |
| `another campaign process is running in <folder> (it holds campaign.lock). Stop it first, or wait for it to finish.` | One campaign per folder. A lock that nobody holds is no lock, so a stale `campaign.lock` file never blocks. |
| `--resume` refused, `differs in: …` | The finished realizations are from other settings. Run without `--resume` for a new campaign, or restore the settings it lists. |
| `N of M realization(s) were stopped by ELMFIRE's wall-clock limit … and are counted as failed. …` | The probability then under-represents large fires. Run again with a larger `--max-runtime-minutes` (a new campaign folder). |
| `no realization produced a usable trigger boundary (…); nothing to aggregate.` | Every realization failed or never reached the WUI area. `realizations.csv` gives each one's status and message. |
| A realization `failed` with exit code 137 (Linux) | The out-of-memory killer. Run fewer at once (`--parallel 1`). |

## Linux

| Message | Fix |
|---|---|
| `The type initializer for 'OSGeo.OSR.OsrPINVOKE' threw an exception.` (or `…GdalPINVOKE…`), with `Unable to load shared library '…_wrap' or one of its dependencies` beneath it | GDAL 3.10 (`libgdal.so.36`) is not on `LD_LIBRARY_PATH`. `PREACTcli` says so as `ERROR: <command> stopped on an unexpected error: …` and exits 1; `PREACTcli --help` works without it. |
| `Unable to find an entry point named '?' in shared library 'libsumocs'` | The committed SUMO bindings are for Windows. [Regenerate them](building.md#regenerating-the-sumo-c-glue-on-linux) in a local copy. |
| No WindNinja found although it is installed | The search looks for `WindNinja_cli` on `PATH` and under `/opt/WindNinja`, `/usr/local/WindNinja` and `~/WindNinja`, and names where it looked. Set `WINDNINJA_CLI` to the executable, or pass `--windninja`. |

The Linux setup is in [Building: Linux](building.md#linux).
