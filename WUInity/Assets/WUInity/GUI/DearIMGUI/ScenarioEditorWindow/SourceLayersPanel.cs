using System;
using ImGuiNET;
using PREACT.Input;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Fuels, canopy and buildings: the layers ELMFIRE warps onto its case grid, and where to get them.
    /// Workflow step 4, and Data &gt; Fuels, canopy and buildings.
    /// </summary>
    /// <remarks>
    /// These lived at the bottom of the Fire tab under a collapsed header, while the LANDFIRE download that
    /// could supply most of them sat in another window and set none of them. Here they are the step's own
    /// window, with LANDFIRE (US) and the FIRE-RES canopy folder (Europe) as the first things offered.
    /// </remarks>
    public static class SourceLayersPanel
    {
        private static bool _isOpen;
        private static string _readStatus = string.Empty;

        private static readonly string[] FuelModelStandards = Enum.GetNames(typeof(ElmfireInput.FuelModelStandards));

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(560f, 620f));
            if (ImGui.Begin("Fuels, canopy and buildings###SourceLayers", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                PREACTInput input = ScenarioSession.Input;
                if (input == null)
                {
                    ImGui.TextDisabled("No scenario is open.");
                }
                else if (input.WildfireModule.Module != WildfireModuleInput.WildfireModules.ELMFIRE)
                {
                    ImGui.TextWrapped("These are the ELMFIRE fire case's inputs, and this scenario's fire is "
                        + (input.WildfireModule.Module == WildfireModuleInput.WildfireModules.AscImport ? "imported." : "not switched on."));
                }
                else
                {
                    ImGui.BeginDisabled(ScenarioSession.EditingLocked);
                    DrawContents(input);
                    ImGui.EndDisabled();
                }
            }
            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        /// <summary>Restores the source layer paths the case was last built from.</summary>
        public static void ReadSourcesFromCase(PREACTInput input)
        {
            string caseDir = PREACT.Utility.ElmfireCoupling.CaseDirectoryPath(input.RootFolder, input.WildfireModule.ElmfireInput);
            if (input.WildfireModule.ElmfireInput.LoadSourcesFromCase(caseDir, out int applied, out string problem))
            {
                _readStatus = $"Restored {applied} source layer path(s) from {PREACT.Utility.ElmfireCaseBuilder.SourceManifestName}.";
                ScenarioSession.NotifyEdited("source layers");
            }
            else
            {
                _readStatus = problem;
            }
            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, _readStatus);
        }

        /// <summary>The panel's contents, also drawn inside All settings &gt; Fire &gt; Fire model.</summary>
        public static void DrawContents(PREACTInput input)
        {
            ElmfireInput elmfire = input.WildfireModule.ElmfireInput;

            ImGui.TextWrapped("Any CRS and any resolution - each is warped onto the case's grid when the case is "
                + "built, nearest-neighbour for the categorical layers and bilinear for the continuous ones. A layer "
                + "the case already has is kept unless the case is rebuilt from scratch.");

            DrawDownloads(input);

            ImGui.SeparatorText("Fuel");
            Fields.Path("FuelModelFile", () => elmfire.FuelModelFile, v => elmfire.FuelModelFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Choice("FuelModelStandard", ref elmfire.FuelModelStandard, FuelModelStandards);
            Fields.Hint("Which standard the raster holds, and so whether it becomes fbfm40.tif or fbfm13.tif.",
                        "Scott & Burgan 40 is what LANDFIRE and the global products ship.");

            DrawRoadsInFuel(input);

            ImGui.SeparatorText("Canopy");

            //Offered before the individual layers because it is the answer for most European cases: point it
            //at the dataset once and every case gets canopy, instead of four rasters per case or none.
            Fields.Folder("CanopyDatasetFolder", () => elmfire.CanopyDatasetFolder,
                v => elmfire.CanopyDatasetFolder = v,
                "A folder of FIRE-RES pan-European canopy rasters. Supplies whichever of the four layers "
                + "below are not named individually, clipped to this case's grid.");
            Fields.Hint("FIRE-RES, pan-European: nothing is downloaded per case, the domain is cut out of the",
                        "rasters. They hold real units where LANDFIRE holds scaled integers, so the case forces",
                        "CH_TIMES_10 / CBH_TIMES_10 / CBD_TIMES_100 off for them.");

            Fields.Path("CanopyCoverFile", () => elmfire.CanopyCoverFile, v => elmfire.CanopyCoverFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("CanopyHeightFile", () => elmfire.CanopyHeightFile, v => elmfire.CanopyHeightFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("CanopyBaseHeightFile", () => elmfire.CanopyBaseHeightFile, v => elmfire.CanopyBaseHeightFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("CanopyBulkDensityFile", () => elmfire.CanopyBulkDensityFile, v => elmfire.CanopyBulkDensityFile = v,
                filter: FileBrowser.geoTiffFilter);

            bool anyCanopy = !string.IsNullOrEmpty(elmfire.CanopyDatasetFolder)
                             || !string.IsNullOrEmpty(elmfire.CanopyCoverFile)
                             || !string.IsNullOrEmpty(elmfire.CanopyHeightFile)
                             || !string.IsNullOrEmpty(elmfire.CanopyBaseHeightFile)
                             || !string.IsNullOrEmpty(elmfire.CanopyBulkDensityFile);

            if (!anyCanopy)
            {
                //Coloured rather than disabled: this is the default state, and its consequence is one a user
                //would otherwise have to infer from a fire that never crowns.
                Fields.Warn("None set, so canopy is filled with zeros (unless the case already has it): surface fire only.");
            }
            else
            {
                Fields.Hint("Whichever of the four are not set are filled with zeros. CH_TIMES_10 / CBH_TIMES_10 /",
                            "CBD_TIMES_100 under Fire behaviour must match the rasters' units.");
            }

            ImGui.SeparatorText("Buildings");
            Fields.Path("BuildingAreaFile", () => elmfire.BuildingAreaFile, v => elmfire.BuildingAreaFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingSeparationFile", () => elmfire.BuildingSeparationFile, v => elmfire.BuildingSeparationFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingNonBurnableFractionFile", () => elmfire.BuildingNonBurnableFractionFile,
                v => elmfire.BuildingNonBurnableFractionFile = v, filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingFootprintFractionFile", () => elmfire.BuildingFootprintFractionFile,
                v => elmfire.BuildingFootprintFractionFile = v, filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingFuelModelFile", () => elmfire.BuildingFuelModelFile,
                v => elmfire.BuildingFuelModelFile = v, filter: FileBrowser.geoTiffFilter);

            int buildingLayers = 0;
            foreach (string layer in new[]
                     {
                         elmfire.BuildingAreaFile, elmfire.BuildingSeparationFile,
                         elmfire.BuildingNonBurnableFractionFile, elmfire.BuildingFootprintFractionFile,
                         elmfire.BuildingFuelModelFile,
                     })
            {
                if (!string.IsNullOrEmpty(layer)) ++buildingLayers;
            }

            //All five or none: ELMFIRE's building spread model needs the whole set, so four is the state worth
            //flagging - it looks like progress and behaves like nothing.
            if (buildingLayers == 0)
            {
                Fields.Hint("None set, so building-to-building spread is off and the fire burns vegetation only.");
            }
            else if (buildingLayers < 5)
            {
                Fields.Caution($"{buildingLayers} of 5 set. All five are needed to switch building spread on,",
                               "so as it stands these will be ingested and then not used.");
            }
            else
            {
                Fields.Hint("All five set, so USE_BLDG_SPREAD_MODEL comes on. Put building_fuel_models.csv",
                            "beside the case with the case builder's copy step.");
            }

            ImGui.SeparatorText("Other");
            Fields.Path("IgnitionMaskFile", () => elmfire.IgnitionMaskFile, v => elmfire.IgnitionMaskFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Where ELMFIRE may start its own ignitions. A painted ignition area becomes this",
                        "automatically, so it is only needed for a mask prepared elsewhere.");

            Fields.Path("BarriersFile", () => elmfire.BarriersFile, v => elmfire.BarriersFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Fuel breaks and other barriers to spread.");

            Fields.Path("SuppressionDifficultyFile",
                () => elmfire.SuppressionDifficultyFile, v => elmfire.SuppressionDifficultyFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Suppression difficulty index, for the extended attack model. Nothing here produces",
                        "one, so it is a raster you bring; without it USE_SDI cannot be switched on.");

            ImGui.Separator();
            if (ImGui.Button("Read sources from the case###ReadSources"))
            {
                ReadSourcesFromCase(input);
            }
            Fields.Hint("Restores these paths from the case's " + PREACT.Utility.ElmfireCaseBuilder.SourceManifestName + ",",
                        "which the builder writes on every build - for a case built before they were set here.");
            if (!string.IsNullOrEmpty(_readStatus))
            {
                ImGui.TextWrapped(_readStatus);
            }
        }

        /// <summary>
        /// The optional road burn: the SUMO network's lanes burned into the fire case's fuel as a spreadable fuel model.
        /// Placed under the fuel because it changes the fuel, and works on what the case build made of it.
        /// </summary>
        private static void DrawRoadsInFuel(PREACTInput input)
        {
            if (!ImGui.TreeNode("Burn roads into the fuel (optional)###RoadsInFuel"))
            {
                return;
            }

            int model = ScenarioDataSteps.EffectiveRoadFuelModel(input);
            Fields.Hint("A fuel map marks roads, and the town around them, non-burnable, which can cut burnable ground into",
                        "islands an ignition never grows out of. This burns the SUMO network's lanes into the fire case's",
                        $"fuel as fuel model {model}, only where the fuel is non-burnable (91-99) and no building model owns",
                        "the cell. It reports the islets before and after: if they match, the roads were not the problem.");

            ImGui.SetNextItemWidth(120f);
            int chosen = ScenarioDataSteps.RoadFuelModel;
            if (ImGui.InputInt("Road fuel model (0: GR1 = 101, or 1 for Anderson 13)###RoadFuelModel", ref chosen))
            {
                ScenarioDataSteps.RoadFuelModel = Math.Max(0, chosen);
            }

            ImGui.SetNextItemWidth(120f);
            double width = ScenarioDataSteps.RoadWidthMetres;
            if (ImGui.InputDouble("Road width, m (0: one cell)###RoadWidth", ref width))
            {
                ScenarioDataSteps.RoadWidthMetres = Math.Max(0.0, width);
            }

            ImGui.Checkbox("Leave building cells to the building spread model###RoadsProtectBuildings", ref ScenarioDataSteps.ProtectBuildingCells);

            ImGui.BeginDisabled(ScenarioSession.IsBusy);
            if (ImGui.Button("Burn roads into the fuel###BurnRoads"))
            {
                ScenarioDataSteps.BurnRoadsIntoFuel();
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Needs the fire case (step 5) and the SUMO network (step 2). Writes "
                    + ScenarioFiles.DownloadsFolder + "/<name>_fbfm40_roads" + model + ".tif, names it as FuelModelFile and puts it "
                    + "in the case; the original layer is kept, and the new raster records which it was.");
            }
            Fields.Caution($"Fuel model {model} spreads fire (slowly): this changes the physics, it is not a bookkeeping fix.");
            ImGui.TreePop();
        }

        /// <summary>The LANDFIRE releases offered, "closest" first; labels and the values they write.</summary>
        private static readonly string[] LandfireReleaseValues = BuildReleaseValues();
        private static string[] BuildReleaseValues()
        {
            var values = new System.Collections.Generic.List<string> { PREACT.Tools.LandfireVersions.Closest };
            values.AddRange(PREACT.Tools.LandfireVersions.Names());
            return values.ToArray();
        }

        private static string ReleaseLabel(string value, int scenarioYear, PREACT.Tools.LandfireVersions.Region region)
        {
            if (value == PREACT.Tools.LandfireVersions.Closest)
            {
                try
                {
                    var r = PREACT.Tools.LandfireVersions.Resolve(value, scenarioYear, region, out string _);
                    return $"Closest to the scenario's year ({scenarioYear}: {r.Name})";
                }
                catch (ArgumentException)
                {
                    return "Closest to the scenario's year";
                }
            }
            foreach (var r in PREACT.Tools.LandfireVersions.All)
            {
                if (r.Name == value) return r.Description;
            }
            return value;
        }

        /// <summary>Where the layers can come from without bringing them: LANDFIRE for the US.</summary>
        private static void DrawDownloads(PREACTInput input)
        {
            ImGui.SeparatorText("Get them");

            PREACT.Math.Vector2d ll = input.Simulation.LowerLeftLatLon;
            PREACT.Math.Vector2d ur = ScenarioDataSteps.UpperRightLatLon(input);
            bool us = ScenarioFiles.IsInLandfireCoverage(ll, ur);
            ElmfireInput elmfire = input.WildfireModule.ElmfireInput;

            if (us)
            {
                //The release: a scenario setting, since a historic fire wants the fuels of its time.
                PREACT.Tools.LandfireVersions.Region region = PREACT.Tools.LandfireVersions.RegionOf(ll, ur);
                int year = input.Simulation.StartDateTime.Year;
                string current = PREACT.Tools.LandfireVersions.Normalise(elmfire.LandfireVersion) ?? PREACT.Tools.LandfireVersions.Closest;
                int index = Math.Max(0, Array.IndexOf(LandfireReleaseValues, current));
                string[] labels = new string[LandfireReleaseValues.Length];
                for (int i = 0; i < labels.Length; ++i) labels[i] = ReleaseLabel(LandfireReleaseValues[i], year, region);
                ImGui.SetNextItemWidth(-200f);
                if (ImGui.Combo("LANDFIRE release###LandfireVersion", ref index, labels, labels.Length))
                {
                    elmfire.LandfireVersion = LandfireReleaseValues[index];
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("[ELMFIRE] LandfireVersion. A release includes the disturbances of its year, so for a historic "
                        + "fire choose the release before it: a later one already has its burn scar.");
                }

                //The e-mail: the user's own, kept per user and never written into the scenario.
                string email = ScenarioDataSteps.LandfireEmail;
                ImGui.SetNextItemWidth(-200f);
                if (ImGui.InputText("Contact e-mail###LandfireEmail", ref email, 128))
                {
                    ScenarioDataSteps.LandfireEmail = email;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    ScenarioDataSteps.SaveLandfireEmail();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("LANDFIRE's product service asks every request for a contact e-mail. Yours is kept for your "
                        + "user in " + PREACT.Tools.LandfireContact.SettingsPath + ", not in the scenario. LANDFIRE_EMAIL "
                        + "is used when this is empty.");
                }
                string trimmed = email.Trim();
                if (trimmed.Length > 0 && !PREACT.Tools.LandfireContact.IsPlausible(trimmed))
                {
                    Fields.Warn("That is not an e-mail address.");
                }
                else if (trimmed.Length == 0 && PREACT.Tools.LandfireContact.Resolve(null, out string _) == null)
                {
                    Fields.Caution("Needed: LANDFIRE files every request under a contact e-mail.");
                }
            }

            bool emailOk = PREACT.Tools.LandfireContact.Resolve(ScenarioDataSteps.LandfireEmail, out string _) != null;
            ImGui.BeginDisabled(!us || !emailOk || ScenarioSession.IsBusy);
            if (ImGui.Button("Download LANDFIRE fuel model and canopy (US)"))
            {
                ScenarioDataSteps.DownloadLandfireFuels();
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(!us
                    ? "LANDFIRE covers the United States only; this domain is outside it."
                    : !emailOk ? "Enter a contact e-mail first: LANDFIRE asks every request for one."
                    : ScenarioSession.IsBusy ? ScenarioSession.BusyTooltip
                    : "Asks LANDFIRE's product service for the " + elmfire.FuelModelStandard + " fuel model and CC, CH, CBH and "
                      + "CBD over the case's padded domain, splits them into single rasters under "
                      + PREACT.Tools.LandfireFuels.Folder + " and names them below, with the canopy scaling flags their units "
                      + "call for. The fuel and canopy the case already has are replaced at its next build. The job queues on "
                      + "their server and can take several minutes.");
            }

            if (!us)
            {
                Fields.Hint("Outside the US: name a fuel model raster of your own, and for Europe the FIRE-RES",
                            "canopy folder below.");
            }

            //Only this step's progress and outcome (the owner stays set after it finishes): a road or population
            //step's status has no business under the fuels.
            if (ScenarioDataSteps.Owner == WorkflowStepId.Fuels)
            {
                ScenarioDataSteps.DrawStatus();
            }
        }
    }
}
