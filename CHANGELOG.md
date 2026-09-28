# Changelog

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

**Imported fires (`AscImport`)**

| What | Before v1 | In v1 |
|---|---|---|
| `[AscImport] StartDateTime` | Ignored by the fire state the evacuation and the end test read. | Honoured everywhere. |
| No-data | A positive no-data value (e.g. 3.4e38) became the latest arrival, so the fire never finished. | No-data and negative values are unburned. |
| Grid edge | Positions up to one cell west or south of the grid counted as inside. | Outside. |

**Weather shown during a run** (does not drive the fire or the evacuation): the FWI codes now use km/h wind
and local noon, the weather CSV's latitude is read (it was always 0), and a scenario without a `WeatherFile` no
longer downloads a year of weather on every run.

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
  everywhere a user sees a fire duration). The `.gfi` painting may record where its grid lies.
- **New outputs**: a `.prj` beside every boundary and campaign raster; the boundary always ends in `.asc`;
  `outputs/run.data` and `outputs/run.fingerprint` in the case. See
  [Output files](docs/output-files.md).

### Changed

- **The `.wui` parser** reads numbers and dates with the invariant culture, splits a line on its first `=`,
  allows comments after a `#`, reads each section on its own so that one bad value no longer loses the rest of the
  file, reports duplicate keys and sections, and a save no longer drops sections of a disabled module. See
  [the input format](docs/input-file-format.md).
- A run stops on its first error, with the modules closed, instead of carrying on and logging every step.
- A run whose cars mostly cannot be put into SUMO stops with an error instead of evacuating nobody.
- A case build is refused while a campaign of the same scenario runs.
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

- **Six `[ELMFIRE]` source-layer keys are not read back from the file**: `SuppressionDifficultyFile`,
  `LandValueFile`, `PopulationDensityFile`, `RealEstateValueFile`, `EnergyReleaseComponentFile` and `PyromesFile`.
  They are saved, but lost when the scenario is opened again. Set them again after opening the scenario, before
  building the case.
- **A `[WildfireModule]` section without an `Enabled` line** loses its ignition points, its painted-areas reference
  and its `[ELMFIRE]` settings when read, and a save then drops them. The GUI always writes `Enabled`; a
  hand-written file should too.
- **Case weather is anchored in UTC.** A case's historical-day weather walks the ERA5 archive, which is on UTC,
  from the scenario's start hour, which is local time; v1 does not convert between them. In the western US a
  13:00 start reads the drawn day's early-morning hours.
- **Southern hemisphere fire seasons.** The fire-weather codes set October to January to an FWI of 0, a northern
  hemisphere winter. In the southern hemisphere those months are the fire season, so its peak days are never
  drawn.
- **k-PERIL** does not cap the length-to-breadth ratio, evaluates the spread ellipse at its parametric angle (so
  wind barely shapes the boundary above about 3 mi/h), and subtracts an upslope wind from the slope term rather
  than adding it. These are for k-PERIL's author; see [Modules](docs/modules.md#trigger-boundary).
- **Linux**: the committed SUMO C# bindings match the Windows SUMO build only
  ([regenerate them](docs/building.md#regenerating-the-sumo-c-glue-on-linux)); WindNinja is found only through
  `WINDNINJA_CLI` or `--windninja`; on the test bench the NFDRS4 library did not load, so dead and live fuel
  moisture fell back to uniform values.
- **FOFEM**: the committed `FOFEM.dll` is a Debug build that needs Visual Studio's debug runtime. Nothing calls
  it yet.
- **Evacuation group masks** are placed on the fire grid by cell and carry no georeference.
- Some messages still name menus that were renamed: *Prepare data* means the Data menu and workflow steps 2–5,
  and *Hazards tab* means Fire > Fire model settings.
