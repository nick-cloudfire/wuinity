# Backlog

Collected from Nick's first use of v1 (Mati and Auburn2, 2026-09-29/30), then worked through in v1.1. What is
done is listed under [Done in v1.1](#done-in-v11), with where to read about it; what is left is first.

## Open

### Decisions for Nick (from the verification cases)

Found by `verify.sh` and reported with their evidence in [verification.md](verification.md#discrepancies). Each is
a modelling choice rather than a bug with one obvious fix, so the checks stay marked as known (XFAIL) until it is
made; the check then shows XPASS, and its `known=` marker and its paragraph come out.

- **D2 — k-PERIL's ellipse is not the fire's.** ELMFIRE-WUINITY (since 3a0a60a) evaluates Anderson's
  length-to-breadth with the midflame wind in m/s (`0.1147*WSMFEFF*WSMFEFF_LOW_MULT`); upstream ELMFIRE, FARSITE and
  k-PERIL use mi/h (0.2566). At case 1's wind the fire's L/B is 1.47 and k-PERIL's 2.62, and a boundary made with
  the fire's own L/B is 8.7 % larger. Pick the unit; the other side and
  [elmfire-cases.md](elmfire-cases.md) ("defined for midflame wind in mi/h") follow.
- **D6 — k-PERIL subtracts an upslope wind from the slope.** `perilData.GetEffectiveWindWithSlope` adds a vector
  towards where the wind comes from to one pointing upslope, so a wind blowing up a slope reduces the effective wind
  (case 2c: 3.03 mi/h where 5.43 is meant; the boundary 5 % larger). Decide the convention (kPERILcore or the
  hand-over in `EvacuationManager`), and state it.
- **D7 — k-PERIL reads ELMFIRE's along-slope rate as a map rate.** `vs` is along the slope; on a 20° slope k-PERIL
  takes the fire to be 6.4 % faster than it crosses the map (15 % at 30°). Conservative. Decide whether to hand
  k-PERIL the map-plane rate.

### Also noted in v1.1, not done

- **Auburn2's ignition point** (38.9005, −121.0700) lies in urban fuel (LF2024 FBFM40 91), 108 m from the nearest
  burnable cell (fuel 183), so a single run from it does not spread; v1.1's case build warns about it and the run now
  fails saying so. Move it onto burnable fuel (the round-2 checks ignited at 38.90650, −121.09022, fuel 165, TU5:
  46.0 acres in 3 h at a uniform 8 m/s from 250°, 22.7 of them crown fire).
- **On Windows, not yet run**: the Unity player build (`build-player.ps1`) and the player itself, `verify.ps1`, and
  the GUI changes of v1.1 (they are compile-checked, and everything head-less is tested on Linux).
- **The GUI font** is Adobe Clean; check its licence before giving a standalone build to others
  ([distribution.md](distribution.md)).
- **WorldPop** downloads the whole country raster (32 min for the USA); a windowed read (GDAL `/vsicurl/`) would
  fetch only the domain.
- **Repository**: push `v1` and `local-aug-wip` to GitHub. Of `local-aug-wip`, `RoadFuelRasterizer` is in v1.1;
  the rest was superseded by v1 and need not be merged.

## Done in v1.1

The CHANGELOG's v1.1 section has the details of each.

### Bugs seen in the GUI log
- **OSM download "done" after an Overpass 504** — a failed download fails the step and nothing after it runs;
  retries with backoff, and a second Overpass server.
- **LANDFIRE "returned nothing usable"** — the step reads the multi-band LFPS GeoTIFF by band description and its
  units from the `.aux.xml`; the contact e-mail is the user's (kept in `tools.ini`), not a hard-coded one.
- **A case build ran ERA5 and WindNinja before failing on the fuel** — the fuel model is checked first.
- **The area-of-interest pick took both corners from one click** — a second corner that makes no area is refused,
  with the reason.
- **The OpenTopography DEM was short of the padded domain** — requested with a margin that covers the UTM grid.
- **Log noise** — "Build the fire case first", the double "wrote", "Painting stopped" on apply, and the SUMO
  warning per destination click are each said once.

### Workflow / GUI
- **Rebuild weather only** — workflow step 5, `PREACTcli build-case --weather-only`, and a run whose fire outlasts the
  case's weather.
- **Namelist floats** — two decimals in the GUI, full precision in the scenario and the namelist.
- **External tools and keys: editable paths** — ELMFIRE, GDAL, WindNinja, SUMO and PROJ, saved per user in
  `tools.ini` and read by WUInity, PREACT.exe and PREACTcli ([distribution.md](distribution.md#the-settings-file)).
- **One WUI-area source** — the evacuation groups; the painted WUI area is gone.
- **Painted initial ignition** — dropped; an old one becomes an ignition point.
- **Selectable LANDFIRE version** — `[ELMFIRE] LandfireVersion`, in the fuels step and `PREACTcli landfire --version`;
  the release is in the layer names and so in `case_sources.txt`.

### Verification
- **Basic verification cases** — `verify.sh` / `verify.ps1` and [verification.md](verification.md): flat ground with
  wind, slope, an evacuation without fire, a small converging campaign. Their discrepancies D1 (Int32 fuel burned
  nothing), D3 and D4 (a group without an area or with missing demographics crashed the run) and D5 (a run that moved
  no car exited 0) are fixed; D2, D6 and D7 are above.

### Packaging
- **Standalone program** — `build-player.ps1` makes `dist\WUInity\WUInity.exe` with PREACT, PREACTcli and ELMFIRE
  beside it; the font is in StreamingAssets; tools are found through the settings file
  ([distribution.md](distribution.md)).

### Repository
- **`RoadFuelRasterizer`** from `local-aug-wip` — ported, as the fuels step's optional "Burn roads into the fuel".
