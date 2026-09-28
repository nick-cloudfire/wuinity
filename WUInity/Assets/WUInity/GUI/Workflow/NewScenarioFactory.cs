using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Evacuation;
using PREACT.Input;
using PREACT.Math;

namespace WUInity.Workflow
{
    /// <summary>What the new-scenario dialog asks for.</summary>
    public sealed class NewScenarioSettings
    {
        /// <summary>The folder the scenario goes in, or (with <see cref="CreateSubfolder"/>) the folder its own folder goes in.</summary>
        public string Folder = string.Empty;
        public bool CreateSubfolder = true;
        public string Name = string.Empty;

        public Vector2d LowerLeftLatLon;
        public Vector2d UpperRightLatLon;

        public DateTime Start = DateTime.Today.AddHours(12);
        public DateTime End = DateTime.Today.AddHours(36);

        public bool Pedestrian = true;
        public bool Traffic = true;
        public bool Wildfire = true;
        public WildfireModuleInput.WildfireModules FireModule = WildfireModuleInput.WildfireModules.ELMFIRE;
        public bool TriggerBoundary = true;
        public bool Smoke = false;

        /// <summary>Start with a "standard" demographics and response curve, which step 8 can then edit.</summary>
        public bool StandardEvacuationInputs = true;
    }

    /// <summary>
    /// Makes the scenario the new-scenario dialog describes. No downloads and no data: the workflow does
    /// those, one step at a time, once the scenario exists on disk.
    /// </summary>
    /// <remarks>
    /// The creator used to run the data steps itself, against an input that had no folder yet (so every file
    /// picked was stored relative to the wrong place), default the fire to an imported one rather than
    /// ELMFIRE, and write nothing until the very end. This writes the .wui first, so from the first moment
    /// every relative path has a folder to be relative to.
    /// </remarks>
    public static class NewScenarioFactory
    {
        /// <summary>What is wrong with the settings, or null when a scenario can be made from them.</summary>
        public static string Validate(NewScenarioSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.Folder)) return "Choose a folder for the scenario.";
            if (string.IsNullOrWhiteSpace(s.Name)) return "Give the scenario a name: every file prepared for it is named after it.";
            if (s.Name.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.Name.Contains(" "))
                return "The name is used in file names, so it cannot contain spaces or any of \\ / : * ? \" < > |.";
            if (s.LowerLeftLatLon.x == 0.0 && s.LowerLeftLatLon.y == 0.0) return "Pick the area of interest on the map (two opposite corners).";
            if (s.UpperRightLatLon.x <= s.LowerLeftLatLon.x || s.UpperRightLatLon.y <= s.LowerLeftLatLon.y)
                return "The area of interest has no size: its north-east corner must be north and east of its south-west one.";
            if (s.End <= s.Start) return "The simulation has to end after it starts.";
            if (File.Exists(ScenarioPath(s))) return ScenarioPath(s) + " already exists; open it instead, or choose another name.";
            return null;
        }

        public static string ScenarioFolder(NewScenarioSettings s)
        {
            string name = (s.Name ?? string.Empty).Trim();
            return s.CreateSubfolder ? Path.Combine(s.Folder, name) : s.Folder;
        }

        public static string ScenarioPath(NewScenarioSettings s)
        {
            return Path.Combine(ScenarioFolder(s), (s.Name ?? string.Empty).Trim() + ".wui");
        }

        public static PREACTInput Create(NewScenarioSettings s)
        {
            var input = new PREACTInput(ScenarioFolder(s));
            SimulationInput sim = input.Simulation;
            sim.Name = s.Name.Trim();
            sim.LowerLeftLatLon = s.LowerLeftLatLon;
            sim.DomainSize = MeasureDomain(s.LowerLeftLatLon, s.UpperRightLatLon);
            sim.StartDateTime = s.Start;
            sim.EndDateTime = s.End;

            input.PedestrianModule.Enabled = s.Pedestrian;
            input.TrafficModule.Enabled = s.Traffic;

            //The module has to be chosen as well as enabled: the parsers reject None for a module that is on.
            input.WildfireModule.Enabled = s.Wildfire;
            input.WildfireModule.Module = s.Wildfire ? s.FireModule : WildfireModuleInput.WildfireModules.None;

            input.SmokeModule.Enabled = s.Smoke && s.Wildfire;
            input.SmokeModule.Module = input.SmokeModule.Enabled ? SmokeInput.SmokeModules.GlobalSmoke : SmokeInput.SmokeModules.None;

            input.TriggerBufferModule.Enabled = s.TriggerBoundary && s.Wildfire;
            input.TriggerBufferModule.Module = input.TriggerBufferModule.Enabled
                ? TriggerBufferModuleInput.TriggerBufferModules.kPERIL
                : TriggerBufferModuleInput.TriggerBufferModules.None;
            if (input.TriggerBufferModule.Enabled && string.IsNullOrEmpty(input.TriggerBufferModule.kPERILInput.OutputName))
            {
                input.TriggerBufferModule.kPERILInput.OutputName = kPERILInput.DefaultOutputName;
            }

            if (s.StandardEvacuationInputs && (s.Pedestrian || s.Traffic))
            {
                input.Population.Demographics["standard"] = new DemographicsInput
                {
                    Name = "standard",
                    AllowMoreThanOneCar = true,
                    MaxCars = 2,
                    MaxCarsProbability = 0.3f,
                    Default = true,
                };

                //Everyone away within an hour and a half of the order, most of them between 60 and 90 minutes:
                //a starting point to replace with the town's own, not a finding.
                var points = new List<ResponseDataPoint>
                {
                    new ResponseDataPoint(0f, 0f),
                    new ResponseDataPoint(3600f, 0.1f),
                    new ResponseDataPoint(4800f, 0.6f),
                    new ResponseDataPoint(6000f, 1f),
                };
                input.Evacuation.ResponseCurves["standard"] = new ResponseCurve(points, "standard");
            }

            return input;
        }

        /// <summary>
        /// Domain size in metres between two corners: UTM when both fall in one zone, the flat-earth
        /// conversion the population tools use when they straddle a zone boundary (Mati sits on 24 E), where
        /// subtracting eastings from different zones is meaningless.
        /// </summary>
        public static Vector2d MeasureDomain(Vector2d lowerLeft, Vector2d upperRight)
        {
            var lower = PREACT.Utility.LatLngUTMConverter.WGS84.convertLatLngToUtm(lowerLeft.x, lowerLeft.y);
            var upper = PREACT.Utility.LatLngUTMConverter.WGS84.convertLatLngToUtm(upperRight.x, upperRight.y);

            if (lower.ZoneNumber == upper.ZoneNumber)
            {
                return new Vector2d(Math.Abs(upper.Easting - lower.Easting), Math.Abs(upper.Northing - lower.Northing));
            }

            Vector2d degrees = new Vector2d(Math.Abs(upperRight.y - lowerLeft.y), Math.Abs(upperRight.x - lowerLeft.x));
            return PREACT.Population.LocalGPWData.DegreesToSize(lowerLeft, degrees);
        }
    }
}
