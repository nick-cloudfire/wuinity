# Changelog

## v1.1 (unreleased)

### Behaviour that changes (read before comparing with v1.0)

- **Campaign fires run until they stop by themselves** (fitted weather, the default): `--hours until-stopped`,
  `SIMULATION_TSTOP` a year, wall-clock limit 4 h; the 240 h cap stays only for hourly (historical-day) weather and
  single runs. **Spotting is off in this mode**: ELMFIRE's stall exit waits for every ember to land (and, before the
  ember-tracker fix in ELMFIRE-WUINITY, never fired with spotting on). Mati realization 13: stops at 100.5 h after
  7 min, 42,034 acres (with spotting, 42,091 acres, still creeping at 258 h). Arrival-time percentiles then cover 96 h
  (later arrivals reported as 96 h).

- **The WUI area is the evacuation groups**, for the case's `wui_area.tif`, k-PERIL and a campaign; a painted WUI area
  is no longer used (see "Case build and fire areas").
- **A fuel raster that is not Int16 is converted or refused** (D1); a fire on one used to burn nothing, exit 0.
- **A fire that does not spread beyond its ignition cells fails a single run** (exit 2; a campaign counts it as not
  threatened). Its fire area is now compared with the ignited cells' area, not with 0, so on a coarse grid (90-120 m
  cells, one cell is 2-3.6 acres) a short or slow fire that stays inside its ignition cell counts as not spread.
- **A run in which no car got into SUMO fails** (D5), however few cars were tried.
- **LANDFIRE "closest"** picks from the releases LFPS serves: a 2026 scenario now gets LF2025, where v1 asked for LF2024.
- **Every k-PERIL boundary changes** (D2, D6, see "k-PERIL"): its spread ellipse is now the fire's, and an upslope wind
  adds to the slope. The head-fire reach upwind is unchanged; flanks and back reach further at low wind. Verification
  case 1: 4690 → 5098 cells (+8.7 %); a Mati realization at an RSET of 116 min: 1.46 → 1.51 km² (+2.9 %, Jaccard 0.97).

### Data downloads and data steps

- **OSM roads**: a download that got nothing now fails the step, so the RouterDb and the SUMO network do not run
  after it ("Building the RouterDb FAILED: Download the OSM data first" after "Downloading OSM roads: done").
  Each Overpass server (overpass-api.de, then the overpass.kumi.systems mirror, or `PREACT_OVERPASS_URLS`) is tried
  in turn, three rounds with a growing pause; a 504, 429, timeout, cut-off transfer or timed-out query is retried,
  and the message says what each server answered. The file is written only when complete. Stop ends the waiting.
- **LANDFIRE**: the step reads the result it downloaded. It used to look for a raster "written since it started",
  and the unpacked file carries LFPS's US timestamp, so a job that succeeded ended "returned nothing usable".
  Bands are found by their descriptions and units by the `.aux.xml`, which set `CC_IN_PERCENT`, `CH_TIMES_10`,
  `CBH_TIMES_10`, `CBD_TIMES_100`. The fuel model is written as Int16 (ELMFIRE reads only that; Int32 or Float32
  fuel gives a fire that does not spread; the case build now makes sure of it for every fuel raster, D1 below). The
  request covers the case's padded domain, not the bare domain.
- **LANDFIRE release**: new `[ELMFIRE] LandfireVersion` (`closest` to the start year by default, or `LF2016`,
  `LF2022`, `LF2023`, `LF2024`, `LF2025` - what LFPS serves; the old list asked for LF2020 fuel, which does not
  exist). Chosen in Fuels, canopy and buildings, or `PREACTcli landfire --version` (the same download from the
  command line; `--update-wui` writes the source-layer keys and flags into the scenario). Layers are named `<Name>_<release>_<stem>.tif`, so
  `case_sources.txt` records the release; `<Name>_<release>_landfire.txt` records the request. The case's old fuel
  and canopy are moved to `inputs/_replaced/` so the next build uses the new ones. FCCS is no longer downloaded.
- **LANDFIRE e-mail**: your own, entered under Fuels, canopy and buildings and kept per user in the tool settings
  file (`tools.ini`, `[User] LandfireEmail`), or `LANDFIRE_EMAIL`; no longer a developer's hard-coded address. An
  e-mail an early v1.1 build kept in `%APPDATA%\WUInity\user-settings.txt` is moved into `tools.ini` once.
- **Area of interest**: a double click, or a second corner on the first or on the same edge, is not taken as the
  second corner; the pick says why and waits.
- **DEM**: OpenTopography is asked for a margin that covers the UTM grid to its corners (Auburn2's padded domain
  was 0.0002 deg short, and its grid corners about 500 m beyond the box asked for).
- **Burn roads into the fuel** (optional, step 4): Nick's road-to-fuel conversion (RoadFuelRasterizer), burning the
  SUMO lanes into the case's fuel as GR1 where it is non-burnable, with the islet counts before and after. When the
  scenario names no fuel layer, the case's own is copied to `downloads/<Name>_fbfm40_original.tif` first, which the
  roads raster then records, so it can be undone.
- The "names no SUMO configuration" warning is given once, not on every destination click; the map rereads the
  network after the roads step builds it.
- `Spatial/Maps/OverpassClient.cs` is removed: nothing used it once the OSM download got its own Overpass client.

### Case build and fire areas

- A case build checks for a fuel model before it downloads or computes anything, and refuses at once without one
  (it used to fail in its validation, after ERA5 and minutes of WindNinja). Workflow step 5 is blocked by step 4
  when `[ELMFIRE] FuelModelFile` names a file that is not there.
- **Rebuild weather only** (workflow step 5, `PREACTcli build-case --weather-only`, and a run whose fire outlasts the
  case's weather): makes `ws/wd/m1/m10/m100` again for the scenario's start, duration and draw and changes nothing
  else; `elmfire.data` keeps every key but its time base and weather band keys. "Rebuild the weather now" used to
  rebuild the whole case.
- `case_sources.txt` records what the weather was made for (`WeatherStart`, `WeatherHours`, `WeatherSeed`,
  `WeatherDay`); a build for another start hour makes the weather again, and step 5 says so.
- Step 5 no longer reports every case's weather one band short (it asked for ceil(hours) + 1 bands).
- The ELMFIRE settings (Fire behaviour, and the fire duration, cell size and padding) show two decimals, three
  significant figures below 1 (`WSMFEFF_LOW_MULT` reads 0.0114, not 0.01), and the exact value in the tooltip when that
  rounds it; nothing is rounded in the scenario or the namelist, and an edit that leaves the shown text as it was keeps
  the exact value.
- **The WUI area is the evacuation groups.** The case build writes `wui_area.tif` as the union of the groups' areas
  (painted masks or shapefiles) on every build; a painted WUI area in the `.gfi` is left out with a note. k-PERIL
  protects the groups (`WuiAreaSource` now defaults to `EvacuationGroupsCombined`; `Raster` reads a `WuiAreaFile` of
  your own, and without one is the groups combined) and no longer falls back to a painted mask. The build no longer
  records `[kPERIL] WuiAreaFile`.
- A campaign checks that the case's `wui_area.tif` is the groups' union as they are now, and refuses a stale one;
  every realization protects that file and a fire that does not reach it counts as not threatened, whatever the
  `WuiAreaSource` (it was checked for `Raster` only, so an unreached combined-groups realization counted as failed).
  A `WuiAreaFile` of the scenario's own that is not on the case grid is refused before the first fire (it used to
  fail every realization in k-PERIL, after its fire and evacuation).
- Evacuation groups from a shapefile keep their rings apart (holes and multipolygons were joined into one outline)
  and are read in the layer's CRS.
- **Fire areas paints only the ignition area.** The WUI-area and initial-ignition brushes are gone; the `.gfi` keeps
  its four layers for older versions but no longer writes either of them. An old `.gfi`'s painted WUI area is
  ignored with one note when the scenario loads. Its initial ignition becomes one `[IgnitionPoint]` at the painted
  cells' centroid (with a note), unless the scenario already has ignition points; the case build places points
  only. Workflow step 6 covers the ignition area and points alone (its Apply to case waits for a painted ignition
  area), step 9 shows the WUI area the build made from the groups (and offers to apply them when the case's
  `wui_area.tif` is stale), step 10's k-PERIL setup protects the groups, and step 13 waits for a `wui_area.tif`
  that is the groups' union. The Fire menu's item is **Fire areas and ignition...**.
- Painting is quieter: "Build the fire case first" (and any other reason there is no grid to paint on) is said once,
  not three or four times per click; **Apply to case** puts the brush down without logging "Painting stopped";
  **Save group areas** is enabled only when something was painted since the last save, and a stroke over cells
  that already hold what it paints no longer counts as unsaved, so a save no longer writes (and logs) every group
  mask twice.
- Workflow step 9 no longer calls a group mask on another grid than the fire grid an error (which blocked a run): the
  group's households and the WUI area read the mask where it lies. It is a warning that painting the group starts
  from nothing on the fire grid.

### Tools and the standalone program

- **Help > External tools and keys takes a path for each tool**: ELMFIRE, the GDAL tools, WindNinja, SUMO (its
  folder or its `bin`) and PROJ's data. Each path is checked as it is typed, and must hold the program. The paths
  are saved per user in `%APPDATA%\PREACT\tools.ini` (Linux: `$XDG_CONFIG_HOME` or `~/.config/PREACT/tools.ini`;
  `PREACT_TOOLS_FILE` names another file). The engine, `PREACT.exe` and `PREACTcli` all read it, so campaigns use
  the same tools as the GUI. The order is: the scenario's `[ELMFIRE]` key or the CLI option, then this setting,
  then the automatic search. A saved path that no longer holds its program is skipped, and the window says so.
- Each tool's **In use** line names the path and where it came from (the scenario, your setting, or the search).
  It used to show the searched GDAL and WindNinja even when the scenario named others. After Save, ELMFIRE, GDAL,
  WindNinja and PROJ apply at once; SUMO applies after a restart, and the window says so. Look again also searches
  for GDAL again.
- Saving `tools.ini` is safe with several programs at once: each save reads the file again under a lock file
  (`tools.ini.lock`), writes through a temporary file of its own and retries a replace a reader holds up; a failed read
  is not remembered as "no settings".
- PROJ's data and SUMO can be set at all (they were read from `PROJ_DATA`/`PROJ_LIB` and `SUMO_HOME`/`PATH` only).
  A SUMO from the settings also sets `SUMO_HOME` for the process. `PREACTcli` puts SUMO's `bin` at the end of
  `PATH` on Windows, as the engine does, for the `gdal.dll` it holds.
- A GDAL library that cannot be loaded no longer stops WUInity from starting. The console and the tools window say
  what to set.
- Every "not found" for a tool - ELMFIRE for a run or a campaign, the GDAL tools, WindNinja (a case build's wind, a
  campaign), netconvert, SUMO in workflow step 3, the Fire input tab and the campaign window - now says to set its
  path under Help > External tools and keys, names the key and the file, and says why a saved path is not used. The
  Fire input tab tells a path from your setting from one found automatically.
- **Standalone build**: `build-player.ps1` builds `dist\WUInity\WUInity.exe` with Unity in batch mode (editor script
  `Assets/WUInity/Editor/PlayerBuild.cs`, also under the menu WUInity > Build standalone player). Beside the player
  it puts `PREACT\` (PREACT.exe and PREACTcli), `elmfire\` (elmfire.exe, impi.dll and the fuel tables), `docs\` and a
  README. A build finds ELMFIRE, its fuel tables, PREACTcli and the docs relative to itself. See
  [Distribution](docs/distribution.md).
- The GUI font moved to `Assets/StreamingAssets/Fonts/`; it was read from `Assets/` on disk, which a player does not
  have. Without the font the GUI uses Dear ImGui's own instead of failing.
- A fire case built in a standalone player gets the OpenTopography key the GUI shows. The key is built into the
  player, and the engine used to look for it on disk.
### Map layers and point info

- **View > Map layers > Fire case inputs**: the case's input rasters (fuel, canopy, terrain, weather by band, masks,
  buildings) and the LANDFIRE source layers, one at a time on the map with an opacity and a legend. FBFM40 and FBFM13
  in LANDFIRE's standard colours (taken from LANDFIRE's LF2024/LF2022 legends; GR9, which LANDFIRE maps nowhere, has
  a GR-family colour of ours); quantities in real units under the namelist's scaling flags; aspect and wind direction
  on a cyclic ramp. Read and coloured on a worker, cached by file and write time, decimated above 1024 cells a side.
- **View > Point info**: click the map for lat/lon, UTM, the case grid cell and every input raster's value there, with
  the last run's time of arrival and trigger boundary. Escape stops it.

### k-PERIL

- **k-PERIL is a submodule** (`PREACT/ThirdParty/kPERIL`, [nick-cloudfire/kPERIL](https://github.com/nick-cloudfire/kPERIL)),
  and PREACTcore builds its `kPERILcore` project (netstandard2.1, the algorithm without GDAL). The copy kept in
  `PREACT/kPERILcore` is gone, and with it the risk of the two drifting; its fixes (the ring one cell beyond RSET, a
  hole in the WUI area written as 3) are in the kPERIL repository. Clone with `--recursive`, or run
  `git submodule update --init --recursive`; the build scripts say so when it is missing.
- **Length-to-breadth (D2)**: ELMFIRE's form, 0.936 e^(0.1147 U) + 0.461 e^(−0.0692 U) − 0.397 with U the midflame
  wind in mi/h, capped at the run's `MAX_LOW` (read from the namelist ELMFIRE ran; 8 for an imported fire). It applied
  0.2566 and 0.1548 to mi/h, so at 4.2 mi/h its ellipse had L/B 2.62 where the fire's had 1.47.
- **Wind and slope (D6)**: both vectors point where they push the fire, so an upslope wind adds to 0.06 x the slope
  (case 2c: 5.43 mi/h, was 3.03). The aspect k-PERIL derives from an elevation raster is a compass bearing.

### Verification

- **Verification cases** (`Verification/`, `verify.ps1`, `verify.sh`, [docs/verification.md](docs/verification.md)):
  synthetic cases run head-less through `build-case`, `PREACT` and `converge-trigger`, each checked against an
  expected value derived independently (Rothermel/BehavePlus, Anderson's L/B, road length over limit, the documented
  convergence rule). They found seven discrepancies. Six are fixed (below and under "k-PERIL"): an Int32 fuel raster
  burned nothing (D1), a group with no area (D3) or no existing demographics (D4) crashed the run, a run that moved no
  car exited 0 (D5), k-PERIL's L/B was not the fire's (D2) and it subtracted an upslope wind from the slope (D6). One
  is open, for a modelling decision, and stays marked as known: k-PERIL reads ELMFIRE's along-slope rate as a map rate
  (D7).
- The cases protect the WUI area `build-case` now makes from the evacuation group (`WuiAreaSource` left at
  `EvacuationGroupsCombined`) and check it against the WUI box, instead of writing `wui_area.tif` themselves; the
  group's mask covers exactly the box's cells, also where the box's edges fall on cell centres (case 2c).
- **Fuel models are stored as Int16 (D1).** The case build writes `fbfm40`/`fbfm13`, `bldg_fuel_model`, `pyromes`
  and every fuel raster a namelist names (a variant, roads burned in, one carried or re-cut onto a new grid) as
  Int16, the only type ELMFIRE reads them from, and rewrites one the case kept in another type, keeping the original in
  `inputs/_replaced/` (the rewrite swaps the files in one step, so a locked file leaves the raster as it was). The validation, a
  run and a campaign refuse a fuel raster that is not Int16, naming it; it used to build, validate and run, and
  burn nothing.
- **Demographics (D4)**: a group whose `Demographics` names none that exists gets the scenario's default, and a
  scenario without any `[Demographics]` the built-in values, as the checklist says. The run used to stop on a
  `NullReferenceException` while it was being set up. A lone group without `MaskFile` or `ShapeFile` (D3) runs with
  every household in it, as the checklist says (round 2's group areas fixed it; now tested).
- The Fire behaviour page and `ElmfireNamelistInput` describe `MAX_LOW` (the cap on the fire's length-to-width ratio)
  and `WSMFEFF_LOW_MULT` (ft/min to mi/h in that correlation) for what they are; they were called the bound and the
  multiplier of a "low-wind branch". [Trigger campaigns](docs/trigger-campaigns.md#convergence) says that the first
  boundary, not the first comparison, leaves the streak as it is.
- **A fire that did not spread is said so on every grid.** The check was "0 acres", but ELMFIRE reports the cells it
  ignited, and one 30 m cell is 0.2 acres: Auburn2's ignition in urban fuel 91 ran "successfully" with nothing spread.
  A fire no larger than its ignition cells now fails a single run (a campaign counts it as not threatened), and the
  case build warns about an ignition point on non-burnable fuel, with the distance to the nearest burnable cell.
- **A run in which no car got into SUMO fails (D5)**: when its time loop ends with cars tried and none injected, the
  run reports "None of the N car(s) of this run could be put into SUMO …" and `PREACT.exe` exits 2. The 90 % rule
  only judges after 25 cars, so a small run used to end successfully with nobody evacuated.

## v1.0

The first versioned release of WUInity / PREACT. It is compared here with the code as it stood before the
v1 work (commit `8d90a440`). v1 fixes a number of errors that changed results, so **read the first two
sections before comparing v1 output with earlier runs**.

### Results that change compared with pre-v1 runs

Magnitudes are from the Mati (Greece) case and the shipped examples, measured on the Linux test bench.

**Trigger boundary (k-PERIL)**

| What | Before v1 | In v1 | Size of the change |
|---|---|---|---|
| Orientation | The fire rasters were handed to kPERILcore in the engine's own array layout, which it reads rotated by 90°: every boundary lay on the wrong side of the WUI area. | Handed over in kPERILcore's layout; a fire spreading east gives a boundary extending west, upwind. | Mati realization 1: 2.23 km² → 4.75 km² (overlap between old and new, Jaccard 0.46). Realization 2: 6.46 → 6.71 km², its centre about 0.9 km further north, onto the upwind side (Jaccard 0.54). **Every boundary and every campaign probability raster made before v1 is affected.** |
| Wind speed | The 10 m open wind (`ws.tif`), where k-PERIL's length-to-breadth formula expects midflame wind. | ELMFIRE's own midflame wind (`mfws`), converted from ft/min to mi/h. | In one Mati realization the mean went from 14.4 to 2.75 mi/h. Because kPERILcore evaluates the ellipse at its parametric angle, the boundary moved by only 1–2 %. |
| Wind in a campaign | Each realization's boundary read the case's historical 72-band wind, not the weather its own fire burned under. | Its own weather, each cell taking the band at the time the fire reached it. | Depends on how far the drawn weather differs from the case's. |
| Slope and aspect in a campaign | The scenario's first `[Landscape]` raster. On Mati it covered 64 % of the fire grid; the rest counted as flat. | The case's `slp.tif`/`asp.tif`, covering 100 %. | Most visible where the old landscape did not reach. |
| Slope and aspect in a single ELMFIRE run | The scenario's `[Landscape]`, which a case built with `PREACTcli build-case` did not repoint: Nick's `mati.wui` sampled its 616 × 590 DEM over 63.9 % of the fire grid. | The terrain the fire burned on, the case's `dem/slp/asp.tif`, whatever `[Landscape]` names. | Mati with the old landscape: the boundary is now cell for cell the one a scenario pointed at the case gets. |
| No evacuation arrivals | A boundary was computed from a zero egress time: the WUI area itself, which a campaign then averaged in, pulling the probability inward. | Refused, with an error; `PREACT.exe` exits 2 and the realization counts as failed. | Campaigns with any such realization. |

**Evacuation**

| What | Before v1 | In v1 | Size of the change |
|---|---|---|---|
| Households reacting to the fire | Inert: every household looked up the same grid cell, the reaction only postponed departures, and the distance was to the final burn scar. | On by default (`[MacroHouseholdSim] ReactToFire`): a household leaves as soon as the current fire front is within 500 m of its home, if that comes before its drawn response. | Mati with a synthetic fire and a 2 h order: 0 → 568 of 784 households left early. Mati with ELMFIRE and the order at +6 h: 146 households (220 people). WRSET and every boundary move. `ReactToFire=false` gives the old behaviour. |
| Destination `MaxFlow` | The flow was computed as arrivals × 3600, so any `MaxFlow` above zero turned away every car after the first. The reported flow column was wrong the same way (Mati: 3,344,400 veh/h). | Cars are admitted at `MaxFlow`/3600 per second, with a minute's worth in reserve. | Any scenario with a `MaxFlow`. Mati's reported peak flow: 1404 veh/h. |
| SUMO's clock | SUMO was not stepped until the first car existed, so the first cars of a run got a 46–144 s head start, and a lull in departures did the same. | Stepped every step, at the simulation's step length. The traffic output starts at t = 0. | Small where departures are continuous. |
| Reproducibility | `RandomSeed` did not make a run reproducible (Mati, seed 42, three runs: 929, 940 and 926 cars). | One seeded generator per run; modules step one after another. | Same seed, identical output files. |
| The simulation clock | Accumulated in single precision. | Exact. | 142 s of drift per simulated day at a 0.2 s step, 746 s at 0.1 s; none at 1 s. |
| `Absolute` response curves | The group's order time (from the scenario start) was added to them again, and a save corrupted them. | Read as calendar times, whatever the order time. | A shift equal to the order's offset from the start. None for Lytton, whose order is at the start. |
| Destination draws | A draw beyond the end of a `DestinationsCDF` that ends below 1 went to the first destination. | To the last. | Only CDFs ending below 1. |
| Several runs (`PREACT <file> <n>`) | The convergence test was one-sided (a falling average always converged), and totals carried over between runs in one session. | Absolute relative change; a miss resets the streak; totals reset. | Batches stop at a different run. |
| SUMO network zone | netconvert chose the UTM zone of the extract's centre; a network across a zone edge (Mati, at 24° E) put fire road closures and re-routing about 500 km off. | Built in the simulation's zone; a network in another zone is reported when the run starts. | Networks near a zone edge, once rebuilt. |
| Roads closed by fire | Straight edges, which carry no shape of their own in SUMO's network, were never closed. | Take their lane's or their junctions' line. | Scenarios with fire over straight roads. |
| Smoke (`GlobalSmoke`) | The ramp was multiplied by 8700 m²/kg before the speed formula; `Roxborough_global_smoke` did not start. | The ramp is the extinction coefficient (1/m). | At 0.2 /m the speed factor goes from 0.50 to 0.57. |

**Trigger campaigns**

| What | Before v1 | In v1 | Size of the change |
|---|---|---|---|
| Fire-weather index of the ERA5 archive | Computed from the wind in m/s, the noon hour's rain alone, at 12:00 UTC. The weather pool was biased toward drought and away from wind. | km/h wind, the rain of the preceding 24 h, 12:00 local standard time. Old archives are re-derived once. | Mati archive: maximum DC 3049 → 928, BUI 609 → 257, FWI 42.9 → 81.1. The fitted pool's mean wind 4.99 → 7.49 m/s. |
| Fire-weather season | The FWI was set to 0 for January and October to December everywhere, and the drought codes used the day lengths of 46° N: south of the equator the fire season's peak days could never be drawn, nor the autumn wind events of all-year fire climates such as California's. | Derived all year; south of the equator the DMC and DC use the southern day lengths of Lawson and Armitage (2008), as the `cffdrs` package does. Northern codes are unchanged. Archives are re-derived once (`archive_format=3`). | Mati archive: no annual peak and no pool day changes. A campaign's settings identity changes with the archive format, so it starts a new campaign folder. |
| Burn probability | Counted only over realizations that produced a boundary. | Over every completed fire. | Lower wherever fires often missed the WUI area. |
| Fires that never reach the WUI area | Counted as failed, after a full evacuation run. | `not-threatened`: no evacuation run, left out of the trigger probability, counted in the fire statistics. | The trigger probability's denominator is unchanged. |
| Each realization's evacuation seed | The base scenario's `RandomSeed` (often 0, a clock seed). | `seed + 2,000,000 + index`, recorded per realization. | Evacuations are now reproducible. |
| Resuming | The GUI resumed by default, reusing realizations from a campaign with other settings. | Only realizations from the same settings are reused. | Old campaigns may mix settings. |
| Missing weather | A failed archive, an empty pool, a failed realization weather or a missing WindNinja fell back to uniform weather with at most a warning. | Stops the campaign before its first fire, or fails the realization, unless `--allow-uniform-weather`. | – |
| Wall-clock limit | `MAX_RUNTIME` 999999 s bounded nothing. | `max(60, 2 × hours)` minutes per fire. A fire stopped by it counts as failed, and the campaign warns that large fires are then under-represented. | – |
| Ignition cells | Anderson 13 fuel 14 could be drawn as an ignition. | Only burnable fuel: not ≤ 0, 91–99 or 14. | FBFM13 cases. |
| Fire duration | The docs recommended a very large `SIMULATION_TSTOP` (thousands of hours). | 1 to 240 hours, default 72. | Much shorter runs. On Mati a 72 h fire had 96 % of its area by 48 h and 99.5 % of the burnable domain by 72 h. |

**ELMFIRE cases**

| What | Before v1 | In v1 | Size of the change |
|---|---|---|---|
| Case grid | Could fall short of the padded domain, clipping the fire at its edge. | Re-cut to cover it, on whole cells; the old grid is set aside in `inputs/_previous_grid/`. | Mati: 566 × 541 → 704 × 680 cells of 30 m. |
| Namelist | Kept between builds, hand edits included. | Written again from the scenario's settings on every build; a hand-edited one is set aside as `elmfire.data.kept-<time>`. | Hand edits no longer apply unless moved into Fire behaviour or a `NamelistTemplate`. |
| Class layers carried over a re-cut | Interpolated. | Nearest neighbour. | Rebuilt cases with building, WUI, barrier or pyrome layers. |
| Non-square GeoTIFF pixels | Read as square. | Kept. | Mati's DEM (27.592 × 27.616 m): about 14 m across the grid. |
| Sun on the terrain in Nelson's dead fuel moisture | The sun position was computed for the mirrored longitude (east read as west): 3.2 h late at Mati, and in the Americas below the horizon at local noon, so the sticks there got no terrain factor in daylight. | At the domain's longitude, on the archive's UTC hour. | Mati, 3 h from 12:35 on 2007-08-25: domain-mean 1-h moisture +0.02 point, its spread across the domain (standard deviation) 0.075 → 0.046 point. Larger in the Americas; not measured (no US archive on the test bench). |
| Hour of day of a historical-day weather series | The scenario's local start hour was read as a UTC hour of the ERA5 archive: every band 3 h late at Mati in summer (2 h in winter), 7–8 h early in California, where a 13:00 fire burned under the drawn day's early morning. The logged burning period was on UTC too. | The local start hour, daylight saving included, converted to UTC with the domain's time zone; band 1 of a 13:00 start is 10:00 UTC at Mati in August. The recorded weather anchor is that UTC instant. | Mati, 3 h from 12:35 on 2007-08-25: bands 12–14 → 09–11 UTC, mean dead moisture 4.4/7.3/10.3 → 5.9/9.2/11.0 % (1/10/100 h). Rebuild a case's weather (`--rebuild`, or Update the case with Rebuild existing layers) to pick it up. A historical-day campaign gets a new settings identity. |

**Imported fires (`AscImport`)**

| What | Before v1 | In v1 |
|---|---|---|
| `[AscImport] StartDateTime` | Ignored by the fire state the evacuation and the end test read. | Honoured everywhere. |
| No-data | A positive no-data value (e.g. 3.4e38) became the latest arrival, so the fire never finished. | No-data and negative values are unburned. |
| Grid edge | Positions up to one cell west or south of the grid counted as inside. | Outside. |

**Weather shown during a run** (does not drive the fire or the evacuation): the FWI codes now use km/h wind
and local noon, the weather CSV's latitude is read (it was always 0), a scenario without a `WeatherFile` no
longer downloads a year of weather on every run, and the record (UTC) is read at the hour the scenario's local
clock stands for — it was read at the same number, 3 h later in the day at Mati in summer and 7 h earlier in
California.

### Actions required after upgrading

1. **Run `build.ps1` (or `build.sh`) before opening Unity**, and after every pull that changes `PREACT/`. The
   engine DLLs are no longer committed. Delete files a previous checkout left in
   `WUInity/Assets/PREACT/Release/` that are no longer built (such as `template.data` or the `gdalconst`
   wrappers).
2. **Rebuild ELMFIRE** from the submodule, now at `a7fb9d6` (`git submodule update --init`, then
   `make_windows.bat`). Every run asks ELMFIRE for the midflame wind, which older builds refuse — see
   [Building ELMFIRE](docs/building.md#building-elmfire).
3. **Rebuild existing ELMFIRE cases** (workflow step 5, *Update the case*). A grid that does not cover the padded
   domain is re-cut. If you edited `elmfire.data` by hand, the build sets it aside as `elmfire.data.kept-<time>`
   and says so: move the edits into Fire > Fire behaviour, or name the file as `[ELMFIRE] NamelistTemplate`.
   *Update the case* keeps a case's existing weather; to give it weather on the corrected clock and sun (see the
   ELMFIRE cases table), delete `inputs/ws.tif`, `wd.tif`, `m1.tif`, `m10.tif` and `m100.tif` first — only the
   weather is then made again. The case's ERA5 archive is re-derived once (`archive_format=3`) when next read.
4. **Paintings made on an older grid**: step 6 offers **Move painting onto the fire-case grid**, which writes a
   new `.gfi` beside the old one.
5. **Campaigns before v1 cannot be resumed**: their folder layout, seeds and settings identity are new. Run them
   again. Their files (`_output/trigger_probability*.asc`, `_output/0_trigger_<n>.asc`, `_output/logs/`,
   `_elmfire/<n>/`) can be kept for comparison or deleted.
6. **Scripts** that call the command-line tools:
   - `PREACTcli probabilistic-trigger` is gone; use `converge-trigger`. `--tstop` is now `--hours`; the other
     retired flags are listed in [Command-line tools](docs/command-line-tools.md#converge-trigger--probabilistic-trigger-campaign).
     Unknown or malformed options are refused (exit 2) instead of ignored.
   - A campaign writes into `_output/campaign_<Name>_<hash>/`, not into `_output/`.
   - `PREACT.exe` exits 0 (success), 1 (nothing run) or 2 (a run reported an error); check it. Its
     `batchSize` argument is ignored.
7. **Scenario files** load as before. Retired keys and sections are reported once and not written again on save
   ([list](docs/input-file-format.md#retired-keys-and-sections)); `[ELMFIRE] SimulationTstopSeconds` is read and
   saved as `SimulationTstopHours`. To compare with a pre-v1 run, set `[MacroHouseholdSim] ReactToFire=false`.
   A `[kPERIL] WindSpeedFile` that an earlier run wrote into the scenario is no longer needed; step 10 offers to
   clear it.

### New

- **The Scenario workflow panel**: thirteen steps from an empty folder to a trigger campaign, each with its
  status, its problems and a button that does it; menus ordered the same way; a short New scenario dialog; a
  Results window and Results > Show on map for any result raster; Help > External tools and keys; unsaved-change
  tracking with prompts before anything is discarded, on quit and when Play stops. See
  [Getting started](docs/getting-started.md).
- **ELMFIRE cases built for anywhere**: terrain from OpenTopography, LANDFIRE fuels and canopy in the US (or your
  own rasters elsewhere), ERA5 weather through WindNinja and Nelson, a namelist generated from the scenario, and
  content-based reuse of an identical earlier fire. See [ELMFIRE cases](docs/elmfire-cases.md).
- **Trigger campaigns rebuilt**: one folder per set of settings (`campaign.json`, a lock, snapshots of the
  template and fuel tables), `--resume`, `--resume-only` and `--inspect`, the statuses `ok`, `not-threatened` and
  `failed`, `realizations.csv` and the weather reports, a per-fire wall-clock limit, and cancellation that stops
  every child process. See [Trigger campaigns](docs/trigger-campaigns.md).
- **The head-less tools on Linux**: `PREACT`, `PREACTcli`, the tests and ELMFIRE (see
  [Building: Linux](docs/building.md#linux)).
- **Build scripts** `build.ps1` and `build.sh`.
- **New scenario keys**: `[MacroHouseholdSim] ReactToFire`, `FireReactionDistance`, `FireReactionUpdateInterval`;
  `[AscImport] MidflameWindSpeedFile`; `[kPERIL] WindBandSeconds`; `[ELMFIRE] SimulationTstopHours` (hours
  everywhere a user sees a fire duration). A `.gfi` painting records the grid it was painted on (the GUI writes
  it on every save, move and copy), and a painting of the right size recorded elsewhere is refused by the build,
  a run and the painter; one without the record is matched by size, as before.
- **View > Map layers > Fire grid outline** draws the edge of the fire-case grid on the map.
- **New outputs**: a `.prj` beside every boundary and campaign raster; the boundary always ends in `.asc`;
  `outputs/run.data` and `outputs/run.fingerprint` in the case. See
  [Output files](docs/output-files.md).

### Changed

- **The `.wui` parser** reads numbers and dates with the invariant culture, splits a line on its first `=`,
  allows comments after a `#`, reads each section on its own so that one bad value no longer loses the rest of the
  file, reports duplicate keys and sections, and a save no longer drops sections of a disabled module. See
  [the input format](docs/input-file-format.md).
- The `[ELMFIRE]` source layers `SuppressionDifficultyFile`, `LandValueFile`, `PopulationDensityFile`,
  `RealEstateValueFile`, `EnergyReleaseComponentFile` and `PyromesFile` are read back when a scenario is opened.
  They used to be saved and then lost, so the next save dropped them.
- A `[WildfireModule]` without an `Enabled` line, or with `Enabled=false`, keeps its ignition points, its
  painted-areas reference, its `Module` and its `[ELMFIRE]` settings through a load and a save (a missing `Enabled`
  means off, with a note); they used to be lost. A save also keeps the section of a module option that is not
  selected (the `[ELMFIRE]` of a scenario switched to `AscImport`, a `[GlobalSmoke]` with smoke set to `None`), and a
  module's sections are read even when its own header is missing. `[ELMFIRE]` is read whatever the fire module,
  so `build-case` and a campaign use the scenario's case settings, not the defaults, when the fire is off.
- **Nothing in a `.wui` is ignored without a word.** An unknown key or section, a retired one and a line that is
  not `Key=Value` are reported once when the scenario is read, with the closest known name, and are not written on
  save; they used to vanish silently. An unreadable `[ELMFIRE]` value (it was dropped silently) and an unreadable
  `[ElmfireNamelist]` value (it was critical) are both reported and keep their default. The examples no longer
  carry the retired `DesiredLatLon` and `VisibilityAffectsSpeed`.
- A run stops on its first error, with the modules closed, instead of carrying on and logging every step.
- A run whose cars mostly cannot be put into SUMO stops with an error instead of evacuating nobody.
- A case build is refused while a campaign runs on the case - of the same scenario, or of any other: every campaign
  also holds the case folder's `campaign.lock` (shared, so several can run on one case), which a build from any
  folder sees.
- **A run fits a namelist's weather band keys to the case's weather**, as a campaign does per realization:
  `NUM_METEOROLOGY_TIMES` = the bands `ws.tif` has, `METEOROLOGY_BAND_START` = `METEOROLOGY_BAND_STOP` = 1, and it
  logs what it changed. Running a kept hand-edited namelist (`elmfire.data.kept-<time>`, whose keys say 72) on a
  case with 8 or 24 bands aborted ELMFIRE with `slice band end (72) is outside the bounds of (1, 8)`. The weather
  check is made on the namelist as it will run, and a template's fire longer than the case's weather is refused
  before ELMFIRE with both numbers. The generated `elmfire.data` writes `METEOROLOGY_BAND_STOP = 1` too. The fire
  is the same (Mati: identical boundary), but the run's namelist text changes, so the first run of an existing
  case computes its fire again. `[ElmfireNamelist] MeteorologyBands` now only sets what the generated file says.
- **The building fuel table.** With the building spread model on, the builder copies ELMFIRE's own
  `build/source/building_fuel_models.csv` into the case's inputs like `fuel_models.csv` (a table already there is
  kept), and refuses the build before it makes anything when no table can be had; a run or a campaign whose
  namelist needs it gets ELMFIRE's, or is refused before ELMFIRE starts. A case without it used to pass its build
  and then stop ELMFIRE at start-up (`Problem opening building fuel model table file`).
- **Re-cutting a namelist's rasters** writes the re-cut copy before it moves the original into `_previous_grid/`, so
  a failed warp leaves the raster where it was; and a raster the scenario itself names (its `[Landscape]` slope,
  say) is never re-cut in place - the namelist gets `inputs/<stem>_<cols>x<rows>.tif` and the build names the key
  to point at it.
- **`PREACTcli build-case`** prints the keys that point the scenario at the case (`[Landscape]` dem/slp/asp,
  `[kPERIL] WuiAreaFile`, the `[Weather]` anchor) as they go into the `.wui`, one block per section, and only those
  it does not already say; the new `--update-wui` writes exactly those into the `.wui` and nothing else (see
  [Command-line tools](docs/command-line-tools.md#build-case--build-a-scenarios-elmfire-case)). `--help`, `-h` and
  `help` print the usage and exit 0 (they were "Unknown command", exit 2), without loading GDAL; with no GDAL at
  all a command now ends with a message and exit 1 instead of an unhandled crash. `PREACT --help` prints its usage,
  and "Simulation run executed" is printed only after a run.
- A module section without an `Enabled` line (the module is off) is a warning in the workflow panel and a *[note]*
  in the scenario check, saying what it means ("the fire module is off and the scenario runs without a fire") and
  what to write; it was one more default among the notes. A line the parser does not read is quoted as written,
  spaces included.
- A boundary's or campaign raster's `.prj` is written from the zone's definition for a WGS 84 / UTM grid when GDAL
  cannot find PROJ's database, and a `.prj` that cannot be written is a warning in the log; it used to be left out
  in silence, and the Results window then marked a new boundary *(earlier version)*.
- A realization's scenario names its own fire's fireline intensity and fuel in `[AscImport]`, or clears them,
  never the base scenario's.
- A scenario starting in the hour that happens twice at the end of daylight saving has its run clock on the same
  occurrence as its case weather's band 1 (the first); the run's UTC start was an hour later.
- **Leaving Play mode during a GUI run** waits for the engine's worker, not for the run's task, whose completion
  needs the player loop the wait blocks: the editor froze for 20 s and then said "Unsaved changes are lost". The
  save question now comes within seconds and saving works. The campaign window handles a process exit on the main
  thread, and a stop during "Checking for an earlier campaign..." starts no campaign.
- The fire grid outline hides on the world map whatever is running. *Copy scenario to...* offers no Stop it would
  ignore, opens no copy after a quit asked it to stop, and its question about unsaved group strokes says what
  happens to the original.
- NuGet's own warnings (NU1xxx - a restore resolving another version, a feed that cannot be reached, the
  vulnerability audit) stay warnings under the projects' warnings-as-errors; the compiler's warnings are still
  errors.
- "Households/people culled ... 0/0" is a log line, not a warning.
- **Stopping a case build** (Stop, or quitting) kills its WindNinja and starts no other: the build writes no wind
  at all (it used to finish on a uniform field), leaves the scenario as it was, and the next build carries on. A
  stopped or converged campaign starts no further WindNinja band. A build that fails after re-cutting the grid
  leaves `inputs/_previous_grid/carry_pending.txt`, and the next build carries the old grid's layers.
- The Results window lists the rasters, not the `.prj`, `.aux.xml` or `.ovr` beside them; it marks a boundary
  without a `.prj` as from an earlier version, and step 13 and the Results window say when a campaign predates
  v1's k-PERIL fix and evacuation seeds - such a campaign is not reused. Renaming an evacuation group keeps its
  painted cells. Painted areas and `.lcp` landscapes open read-only. The GUI finds the case folder and a relative
  `NamelistTemplate` the way the engine does.
- `.gitignore` covers `WUInity/imgui.ini` (each user's window layout), ImGui's `imgui_log.txt`, Mono's
  `mono_crash.*`, `*.tmp`, `*.partial` and the realization scenarios a killed campaign leaves beside the `.wui`.
- WindNinja is found on Linux and macOS as `WindNinja_cli` (on `PATH`, `/opt/WindNinja`, `/usr/local/WindNinja` or
  `~/WindNinja`); the search looked for `WindNinja_cli.exe` only, so there only `WINDNINJA_CLI` or `--windninja`
  found it. The "not found" message names where it looked on the platform it runs on.
- Messages name the menus the GUI has: *Data > Build fire case (ELMFIRE)* and *Data > Roads* instead of *Prepare
  data*, *Fire > Fire areas* and *Fire > Fire behaviour* instead of the *Hazards tab*. The header of a generated
  `elmfire.data` says so too, so the first run after a case is rebuilt computes its fire again instead of reusing
  the earlier output (the namelist's text is part of what a reuse compares). The destination editor's *Max arrival
  flow* tooltip says what happens to a car over the limit: it is put back into traffic to the same destination.
- Paths are stored with `/` and resolve on every platform; a file that has moved is found by name nearby.
- On Windows the engine prepends its folders to `PATH` instead of replacing it, so SUMO, the GDAL tools and ELMFIRE
  are found as installed. SUMO is found through `SUMO_HOME` or `PATH`.
- Unity uses OsmSharp 6.2.0, the version the engine is built against.

### Removed

- The `probabilistic-trigger` command, reading pre-generated fire ensembles (`--dir`, `--toa`, ...), shared and
  single-band campaign weather modes, and the other retired `converge-trigger` flags.
- The parallel execution modes of a single process (runs are serial; a campaign runs processes in parallel).
- The `[Events]` section, which was never implemented.
- The QGIS plugin.
- The examples and verification data for fire models that no longer exist: `Examples/Development`,
  `Examples/CFFDRS/Dogrib`, `Examples/ASDRF`, `Verification/`; 83 unreferenced files from the two remaining
  examples.
- The committed engine DLLs, the committed ILGPU copy, and Unity packages nothing used.
- The GUI's Download data, Population editor, Start and scenario-data windows, replaced by the workflow panel.
- The design notes `docs/elmfire-case-automation.md` and `docs/probabilistic-trigger-convergence.md`, replaced by
  [ELMFIRE cases](docs/elmfire-cases.md) and [Trigger campaigns](docs/trigger-campaigns.md).

### Known issues

The Windows GUI's acceptance check is the [manual test](docs/manual-test-v1.md); the head-less paths were run
end to end on Linux.

- **k-PERIL** does not cap the length-to-breadth ratio, evaluates the spread ellipse at its parametric angle (so
  wind barely shapes the boundary above about 3 mi/h), and subtracts an upslope wind from the slope term rather
  than adding it. These are for k-PERIL's author; see [Modules](docs/modules.md#trigger-boundary).
- **Linux**: the committed SUMO C# bindings match the Windows SUMO build only
  ([regenerate them](docs/building.md#regenerating-the-sumo-c-glue-on-linux)).
- **FOFEM**: the committed `FOFEM.dll` is a Debug build that needs Visual Studio's debug runtime. Nothing calls
  it yet.
- **Evacuation group masks** are placed on the fire grid by cell and carry no georeference.
- **Fire-weather day lengths north of the equator** are the standard 46° N ones at every latitude, as before v1:
  the `cffdrs` package uses flatter factors for the DMC south of 30° N (a constant below 10° N) and a constant for
  the DC south of 20° N (southern Florida and Texas, Hawaii, Mexico). v1 applies its latitude bands only south of
  the equator, so as not to change northern results.
