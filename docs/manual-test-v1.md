# Manual test of the v1 GUI (Windows, Unity editor)

This is the check of what a compile check cannot see: the WUInity GUI driving the engine, ELMFIRE and the
campaign CLI on a real machine. It takes an afternoon, most of it waiting for ELMFIRE. Work through it in order;
each step says what to do and what you should see. Anything else is a finding: note the step number, what you saw,
and the Console's text.

Keep the **Console** visible throughout (docked along the bottom). After every step, set its filter to
**Errors only** for a moment: a line containing `EXCEPTION` is always a bug, even if the GUI carried on.

---

## 0. Before you start

1. **Run the build script before opening Unity.** The engine DLLs are no longer committed; Unity finds them only
   after the script has built them.

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1
   ```

   Expected: it ends with the three outputs listed and no `BUILD FAILED`. Run it again after every pull that
   touches `PREACT/`. If Unity was opened before the script ran, close it, run the script, and reopen.

2. **Work on a copy of Mati, never on the original.** In File Explorer, copy `D:\WUINITY\cases\mati_generated`
   to `D:\WUINITY\cases\mati_v1test`. The folders `_output` and `_elmfire` can be left out (they hold earlier
   campaigns and are large); everything else is needed. Everything below that says "Mati" means this copy.

3. The tools the tests use: `elmfire.exe` built at a7fb9d6 (it writes the midflame wind k-PERIL needs), SUMO 1.22,
   the QGIS GDAL tools, WindNinja, and an OpenTopography key (in
   `Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt`, or typed in for the session in step F4).

---

## A. Start-up and layout

1. Open `WUInity/` in Unity, open the scene `Assets/WUInity/Scenes/WUInityMain.unity`, press **Play**.
   - The **Scenario workflow** panel is docked on the left and the **Console** along the bottom; the map fills
     the rest. No window floats.
   - Dragging and scrolling on the map pans and zooms it.
   - The last scenario you had open reopens, and its name is at the right of the menu bar.
2. Drag the workflow panel out so it floats, then **View > Reset window layout**. It docks back on the left.
3. Stop Play and press Play again. The arrangement you left is kept (ImGui keeps it in `WUInity/imgui.ini`).
   Close Unity, delete `WUInity/imgui.ini`, reopen and press Play: the default layout is laid out again.
   `git status` does not list `imgui.ini` (it is ignored).
4. **View > Theme > Dark**, then **Light**: the radio mark follows the theme.
5. **Help > External tools and keys**: ELMFIRE, GDAL, WindNinja, SUMO, PROJ and the OpenTopography and Mapbox
   keys are listed with what was found. **Look again** re-probes without a hitch.

---

## B. Mati: moving the painting onto the fire-case grid

1. **File > Open scenario...** and open `mati_v1test\mati.wui`. Expected rows (the numbers are from Nick's
   August case; yours may differ a little):
   - **5. Fire case (ELMFIRE)** - *Needs attention*, with two warnings:
     - "elmfire/elmfire.data is not the namelist the last build wrote ... The next build ... sets this one aside
       as elmfire.data.kept-<time>" with **Keep running it**;
     - "[Landscape] uses elmfire/inputs/mati_dem.tif (616 x 590) ... The painted areas are on that terrain's
       grid: move them onto the case grid first" with **Move painting onto the fire-case grid**.
   - **6. Fire areas and ignition** - an error: "The areas were painted on a 616 x 590 grid
     (elmfire/inputs/mati_dem.tif); the fire grid (elmfire/inputs/dem.tif) is 566 x 541. Move the painting onto
     it ...", with **Move painting onto the fire-case grid**. Apply to case is disabled with "move them onto the
     fire-case grid first".
   - **13. Trigger campaign** - Blocked by step 6.
   - **Scenario > Check scenario** lists, as notes that do not block anything, the four retired keys in the file -
     `[Evacuation] UseTriggerBufferEvacuation`, `[Weather] DesiredLatLon`, `[TrafficModule] VisibilityAffectsSpeed`
     and `[kPERIL] WindBand` - each "is no longer used: ... It is ignored, and saving the scenario does not write
     it." The Console has the same four lines once each.
2. **Fire > Fire areas (WUI, ignition area)...** The window names the grid (`elmfire/inputs/dem.tif`, 566 x 541),
   says the saved painting is on a 616 x 590 grid, and has a **Move painting onto the fire-case grid** button.
   **Start painting** is disabled, with a tooltip saying to move the painting first.
3. Press **Move painting onto the fire-case grid**.
   - A "Data preparation" window runs "Moving the painting onto the fire-case grid" and logs, among others:
     `WUI area: 7383 -> 6260 cells (5.626 -> 5.634 km2)` and `ignition area: 161543 -> 135734 cells`.
   - `painted_fire_areas_566x541.gfi` appears beside `painted_fire_areas.gfi`; the original is unchanged
     (same size and date).
   - Row 6 reads "WUI 6260 cells, ignition area 135734 cells ... grid 566 x 541" and is no longer in error; the
     menu bar says "mati - unsaved changes".
   - In the Fire areas window, **Start painting** now shows the painted WUI area on the map.
4. **File > Save** (Ctrl+S). The .wui now has `GraphicalFireInputFile=painted_fire_areas_566x541.gfi`.
5. Row 5: decide about the hand-edited namelist. For this test press nothing - let the build set it aside.
   (Pressing **Keep running it** sets `[ELMFIRE] NamelistTemplate=elmfire.data`; the Console then logs - not as a
   warning - that if a build has to re-cut the case grid, every raster this namelist names, hand-made ones such as
   `fbfm40_roads101.tif` included, is re-cut onto the new grid with it.)
6. Row 5 **Update the case** (and **Save** if asked). This takes minutes (WindNinja per weather hour). Expected:
   - The log says the case's dem.tif does not cover the domain padded by 2000 m and re-cuts the grid (about
     704 x 680 cells), moving the old rasters to `elmfire\inputs\_previous_grid`.
   - It also says the painting (566 x 541) was on the grid it set aside and moves it onto the new case grid:
     `painted_fire_areas_704x680.gfi`, WUI about 6260 cells.
   - The Console has a warning "A hand-edited namelist was set aside as elmfire.data.kept-<time>", and row 5 shows
     it with **Run elmfire.data.kept-<time>**.
   - Row 5's terrain warning is gone (the build pointed `[Landscape]` at the case's dem/slp/asp).
   - Row 6 is Done on the new grid; row 10 protects `wui_area.tif`.
7. **File > Save.** Paint a few extra WUI cells (Fire areas > Start painting), **Stop painting**, and *do not* save.
   Row 6 says "Painted strokes not saved yet". Press row 6's **Apply to case**: it asks **Save and apply** - there
   is no "Don't save", because the case is built from the saved file. Press **Cancel**.
8. With the strokes still unsaved, press row 5's **Update the case** and answer **Don't save**.
   After the build the header still says "Unsaved changes" and row 6 still says the strokes are not saved; the
   strokes are still on the map when you Start painting again.
9. **Evacuation > Paint group areas...**: the existing mask shows at once. Paint, close the window, reopen it:
   one window, no doubled controls. With the paint window open, add a second group in **Evacuation > Evacuation
   groups**; it appears in the paint window's list. Paint some cells for each and **Save group areas**: each
   group's MaskFile names its own `evac_group_<name>.asc`. Rename a group that has painted cells: its cells stay
   (in the paint window and in its mask after **Save group areas**), and no `evac_group_<old name>.asc` is written.

---

## C. One simulation, with Stop

Mati needs its SUMO network (row 2) and a fire; if row 11 lists blockers, fix them from their rows first.

1. **Run > Run simulation...** (F5). The window lists what stands in the way, or "Nothing in the way".
2. Press **Save and run (F5)** (or **Run**). The phase reads "preparing" while ELMFIRE computes; the Console shows
   ELMFIRE's progress.
3. During "preparing", press **Stop**. Expected at once: the run ends ("Stopped after N min, before the simulation
   started"), and Task Manager shows no `elmfire.exe` or `mpiexec.exe` left.
4. While "preparing" again, check that the brush cannot paint (Fire areas: Start painting disabled), that
   **File > Open** and **New** are disabled with "Not while a simulation is running", and that **All settings**
   can be browsed tab by tab but its fields are read-only.
5. Run again and let it finish. The window says "Finished in N min (Completed)." Row 11 is Done.
6. A scenario the engine would refuse: open the Rafina scenario from section G (it has no destinations yet) and
   **Run > Run simulation**. The Run button is disabled, its tooltip names what the scenario check requires, and
   ticking **Run anyway** does not enable it (it only skips the workflow's own blockers). Should a run ever be
   refused by the engine all the same, the window says "Not run: the engine refused the scenario - it still
   needs:" with the list, never "Finished in 0.0 min". Reopen Mati afterwards.
7. **The kept namelist.** Row 5 still offers **Run elmfire.data.kept-<time>** (B6). Press it, **Save**, and run
   (F5). The Console says "Namelist weather bands fitted to ws.tif (N band(s)): &MONTE_CARLO METEOROLOGY_BAND_STOP:
   '72' -> '1'; &MONTE_CARLO NUM_METEOROLOGY_TIMES: '72' -> 'N'", where N is the case's weather hours, and ELMFIRE
   burns `fbfm40_roads101` to the end - there is no "slice band end (72) is outside the bounds of (1, N)". Set
   `SimulationTstopHours` (**Fire > Fire model settings...**) above N hours and run again: the run is refused
   before ELMFIRE with "The case's weather cannot run this fire: the weather covers N h (N bands) and the fire
   runs M h ...". Afterwards set the fire duration back, clear `NamelistTemplate` (same page), and **Save**.

---

## D. Results

1. **Results > Results of the last run and campaign...** The window groups what was written: **Last run** (log,
   arrival CSV, trigger boundary, fire arrival), **Campaign**, **Case** (wui_area.tif). No `.prj`, `.aux.xml` or
   `.ovr` file is listed as a result, although they sit beside the rasters. A boundary from before v1 (no `.prj`
   beside it) is marked *(earlier version)*, and a campaign made before v1 has a line saying so.
   **The boundary of C5 is not marked**, and in File Explorer `mati_v1test\_output\0_trigger_boundary.prj` sits
   beside `0_trigger_boundary.asc` (open it: it names `WGS 84 / UTM zone 34N`). If it is marked, or the `.prj` is
   missing, the Console has a warning "... was written without a .prj: ..." - note it as a finding, with that text.
2. **Show on map** beside the fire arrival: the raster is drawn over the domain with a legend line in the window.
   **Hide from map** removes it. **Results > Show on map > Trigger boundary** draws the boundary.
3. **View > Map layers > Markers**: unticking hides the destination and ignition markers; ticking shows them.
   **View > Map layers > Fire grid outline** draws the edge of the fire-case grid in orange (about 2 km beyond the
   domain on every side at Mati); it hides on the world map and, after a re-cut, follows the new grid.
4. **Results > Live output** after the run shows the run's numbers. Open another scenario: Live output then says
   nothing has run for the open scenario yet (the last run was of mati), and the run window's "Last run" is empty.

---

## E. Trigger campaign: start, cancel, resume

1. **Run > Trigger campaign...** opens the campaign window whatever the workflow says. If row 13 is blocked, the
   window's **Run** is disabled and the reason is written beside it. The copy of Nick's case still holds his August
   campaign folders if you kept `_output`: row 13 warns that that campaign predates v1's k-PERIL fix and its
   per-realization evacuation seeds, and Run starts a new campaign beside it - no **Reuse** is offered for it. The
   window also says that every realization is seeded from the campaign seed and its index (the scenario's own
   RandomSeed applies to single runs only).
2. Set **Maximum realizations** to 3 and **Fire duration** to 24 hours. Without WindNinja, tick **Allow uniform
   weather** (the window warns otherwise). Press **Run**. With unsaved changes it asks to save first.
3. While it runs:
   - row 13 reads *Running*, and the workflow header says "A campaign is running." with **Show**;
   - row 5's **Update the case** and the Data menu's steps are disabled: "Not while a trigger campaign is running";
   - close the campaign window, then **Run > Trigger campaign...** again: it opens, showing the running campaign.
4. Press **Cancel** during the first realization. Expected: "Cancelled. Finished realizations are kept"; Task
   Manager shows no elmfire, WindNinja, PREACT or PREACTcli process left.
5. Press **Run** again with the same settings. It asks: **Reuse N realization(s)**, **Start over**, **Back**. Press
   **Reuse**: it resumes with the missing realizations only, and ends "Converged ..." or "Reached the maximum
   without converging".
6. Row 13 names the campaign folder (`campaign_mati_<hash>`), and **Results > Show on map > Trigger probability**
   draws the probability raster.

---

## F. New scenario in the US, with LANDFIRE

1. **File > New scenario...** (Ctrl+N). Choose a folder (e.g. `D:\WUINITY\cases`), name `paradise_test`, keep
   **Make a folder for it, named after it** ticked.
2. **Pick on map**: the dialog hides; click two corners around Paradise, California. It comes back with the size
   (about 9 x 8 km) and says the area is inside LANDFIRE's coverage. Wildfire is ELMFIRE, **Trigger boundary
   (k-PERIL)** on.
3. **Create scenario**. The .wui is written and opened. Row 1 is Done; row 8 is Done ("1 curve, 1 demographics");
   row 10 has no "fire stops after 8 h" warning (a new scenario's fire lasts its time window, at least 24 h).
4. Only if no OpenTopography key file exists: **Help > External tools and keys**, type the key into **Key for this
   session only**. Row 5 no longer says there is no key.
5. Row 4 **Get LANDFIRE fuels and canopy (US)**. After the job, `downloads\landfire\paradise_test_lf_*.tif` exist,
   and **Data > Fuels, canopy and buildings** shows the fuel model and CC/CH/CBH/CBD with the scaling flags set.
6. Row 5 **Build fire case**. It downloads the terrain (with the session key, if that is where it came from),
   warps the layers and writes the weather and namelist. Afterwards `[Landscape]` names the case's dem/slp/asp, and
   row 6 unblocks.
7. Row 6: paint a WUI area and an ignition area, **Save painted areas**, **Apply to case**. Row 6 is Done and
   `wui_area.tif`/`ignition_mask.tif` are in the case.
8. Rows 2 and 3 **Prepare missing**: OSM, RouterDb, SUMO network; then WorldPop and households. During a step, press
   **Stop** in its progress window: the chain ends after the link it is in, and says so.
9. Row 5 **Rebuild everything**, and press **Stop** while the log shows WindNinja solving the weather. Expected: the
   step ends `... STOPPED: The case build was stopped while its weather was being made: no wind was written ...`, within
   seconds; no WindNinja process is left; the case has no new `ws.tif` (not a uniform one either) and the scenario
   is not marked changed. **Update the case** afterwards carries on and finishes.

---

## G. New scenario in Europe

1. **File > New scenario...**, name `rafina_test`, pick two corners a few km apart just west of Rafina, Greece -
   around 38.03 N, 23.97 E, well inside Mati's area. The dialog says the area is outside LANDFIRE's coverage.
   **Create scenario**.
2. Row 4: **Get LANDFIRE fuels and canopy (US)** is disabled ("LANDFIRE covers the United States only"); the row
   explains what fuel raster to name instead. Under **Data > Fuels, canopy and buildings**, name Mati's
   `elmfire\inputs\mati_fbfm40.tif` as the fuel model; row 4 becomes Done.
3. Row 5 **Build fire case** works as in F6.

---

## H. Quitting and leaving Play mode

Every step starts with nothing running. Each makes its edit **first**: while a step or a run is going, every editing
surface is read-only. A quick edit that is easy to check afterwards: **Evacuation > Evacuation groups...**, change
the first group's colour.

1. **Play-stop with unsaved changes.** Make the edit (the header says "Unsaved changes"), then press Unity's Play
   button to stop. Expected: Unity's own dialog "Unsaved changes ... Save them before leaving Play mode?" with
   **Save** / **Don't save**. Choose Save: the .wui on disk has the edit. Repeat and choose Don't save: it has not.
2. **Play-stop during a run.** Press Play, make the edit, then **Run > Run simulation...** and press **Run** (not
   *Save and run*: the edit must stay unsaved). While the phase reads "preparing" (ELMFIRE computing) - and once
   more, on a second try, while the evacuation itself runs - press Play to stop. Expected, each time: within a few
   seconds (not a 20 s freeze), the dialog "Unsaved changes ... Save them before leaving Play mode?"; **Save**
   writes the edit into the .wui. Task Manager then shows no `elmfire.exe`, `mpiexec.exe` or `sumo` process left.
   "Unsaved changes are lost" here is a finding.
3. **Play-stop while a data step runs.** Press Play, make the edit, then row 5 **Rebuild everything**: it asks
   whether to rebuild (**Rebuild**), then whether to save first - answer **Don't save**, so the edit stays unsaved.
   While the step's log shows WindNinja solving the weather, press Play to stop. Expected: a short wait (a few
   seconds: WindNinja is killed at once, and the build stops at its next safe point), then the dialog "Unsaved
   changes ... Save them before leaving Play mode?"; Save writes the edit. (A step inside a link that cannot be
   interrupted - an OSM or DEM download, a RouterDb or netconvert - may run past the 20 s the editor waits: then
   the dialog is "Unsaved changes are lost ... File > Quit, which waits for it, is the way to leave with them
   saved." That is expected for those links, which is why this step uses WindNinja.)
4. **File > Quit while a data step runs, with unsaved changes.** Make the edit, start row 5 **Rebuild everything**
   (answer **Rebuild**, then **Don't save**), and while WindNinja solves press **File > Quit**. Expected:
   - "... is running. Quitting stops it first, then asks whether to save the unsaved changes." - press **Stop it
     and quit**;
   - a prompt "Stopping before quitting: ..." (with **Quit now, without saving** and **Cancel**) while the step
     stops - WindNinja is killed at once, a download under way finishes first;
   - then the save question (**Save** / **Don't save** / **Cancel**); Save writes the .wui, and Play stops.
5. **File > Quit during a campaign**: the same, and afterwards no campaign process is left. Do it once more right
   after pressing the campaign window's **Run**, while it says "Checking for an earlier campaign with these
   settings...": the quit goes ahead and no campaign starts (Task Manager shows no `PREACTcli`).
6. **File > Copy scenario to...** with unsaved painted fire strokes: the copy's painted-areas file contains them
   (open the copy and look at row 6), and nothing was written into the original folder. The "Data preparation"
   window that shows the copy has no **Stop** ("This cannot be stopped; it finishes by itself"). With unsaved
   *group* strokes it asks **Save them here, then copy** / **Copy without them** / **Cancel**, and its text says
   that the copy is opened afterwards, that the first writes the masks into this scenario's folder without
   rewriting its .wui, and that the second loses the strokes from both.

---

## What is known not to be there

- Evacuation group masks are written in simulation-space coordinates, not georeferenced.
