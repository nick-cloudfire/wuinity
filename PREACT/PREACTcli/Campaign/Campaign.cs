using System;
using System.Collections.Generic;
using PREACT.Math;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// A campaign once everything it depends on has been resolved and checked: the base scenario, the ELMFIRE
    /// case, the tools, the weather, and the folder it writes to. Built by <see cref="CampaignSetup"/>; read by
    /// the realization and aggregation code, which never re-resolve anything.
    /// </summary>
    internal sealed class Campaign
    {
        public CampaignOptions Options;

        // ---- the base scenario
        public string BaseWuiPath;
        public string ScenarioDir;
        public string[] BaseLines;

        /// <summary>The .wui's file name without extension.</summary>
        public string BaseFileName;

        /// <summary><c>[Simulation] Name</c>, which names the ERA5 archive and the realizations.</summary>
        public string ScenarioName;

        public DateTime StartDateTime;

        /// <summary>The civil time zone <see cref="StartDateTime"/> is stated in, at the domain's south-west corner.</summary>
        public TimeZoneInfo TimeZone;

        /// <summary>The base scenario's own <c>[Simulation] RandomSeed</c>, which realizations replace with their own.</summary>
        public int BaseRandomSeed;
        public Vector2d CentreLatLon;

        /// <summary><c>[kPERIL] WuiAreaSource</c>: Raster, EvacuationGroupsCombined.</summary>
        public string WuiAreaSource = "Raster";

        // ---- the fire case
        public string ElmfireExe;
        public string TemplatePath;
        public string InputsDir;
        public string GdalBin;
        public string PreactExe;

        /// <summary>The template's lines as snapshotted at campaign start; realizations never re-read the file.</summary>
        public string[] TemplateLines;

        public double SecondsPerBand = 3600.0;
        public double EdgeBufferMetres = 60.0;
        public string FuelStem;
        public string IgnitionMaskStem = ElmfireStems.IgnitionMask;
        public int IgnitableCells = -1;

        /// <summary>
        /// The WUI area every realization protects - the case's wui_area.tif (the evacuation groups' union), or the
        /// scenario's own [kPERIL] WuiAreaFile - with its centroid for aiming the wind. Set by the time a campaign runs.
        /// </summary>
        public string WuiAreaFile;
        public double WuiCentreX, WuiCentreY;
        public int WuiCells;

        /// <summary>The fuel model tables, copied into the campaign folder so no realization reads or writes shared ones.</summary>
        public string FuelTableSource;
        public string BuildingTableSource;
        public string BuildingTableName = ElmfireStems.BuildingFuelModelTable;

        // ---- weather
        public string ArchivePath;
        public int ArchiveEndYear;
        public string WindNinjaExe;
        public MasterGrid Grid;
        public WeatherRasterPipeline.Options Weather;
        public List<HourlyWeatherRow> ArchiveRows;
        public IReadOnlyDictionary<DateTime, LiveFuelMoistureDay> LiveMoistureByDate;
        public List<WeatherStatisticsReport.VariableStatistics> WeatherStatistics;
        public string WeatherStatisticsHeader;

        // ---- identity and layout
        /// <summary>Every setting that decides a realization's result, as sorted key/value pairs.</summary>
        public SortedDictionary<string, string> Settings = new SortedDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Recorded in the manifest but not part of the identity: they do not change a realization.</summary>
        public SortedDictionary<string, string> Information = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public string SettingsHash;
        public string Folder;
        public string RealizationsDir;

        /// <summary>Where the snapshotted tables live, as MISCELLANEOUS_INPUTS_DIRECTORY.</summary>
        public string TablesDir => Folder;

        public string RealizationDir(string id) => System.IO.Path.Combine(RealizationsDir, id);

        /// <summary>The name a realization's PREACT run and its outputs go by: unique per campaign.</summary>
        public string RealizationName(string id) => ScenarioName + "_" + SettingsHash.Substring(0, 8) + "_" + id;
    }
}
