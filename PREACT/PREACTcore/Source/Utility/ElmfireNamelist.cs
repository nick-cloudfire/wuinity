using System;
using System.Collections.Generic;
using System.Globalization;

namespace PREACT.Utility
{
    /// <summary>
    /// The namelist group and key names the campaign driver patches per realization.
    /// </summary>
    /// <remarks>
    /// Here, beside <see cref="ElmfireNamelist.SetKeyInGroup"/>, because that is the only thing that consumes
    /// them. They used to live in <c>ElmfireRealizationWriter</c>, a class the campaign never actually used —
    /// the driver patches the namelist itself — so deleting the dead writer took the live constants with it.
    ///
    /// Only the keys the driver sets are listed. The full set a generated namelist carries is
    /// <see cref="ElmfireNamelistBuilder"/>'s business, and duplicating them here would be two lists of
    /// ELMFIRE's key names to keep in step.
    /// </remarks>
    public static class ElmfireNamelistKeys
    {
        public const string InputsGroup = "INPUTS";
        public const string OutputsGroup = "OUTPUTS";
        public const string TimeControlGroup = "TIME_CONTROL";
        public const string SimulatorGroup = "SIMULATOR";
        public const string MonteCarloGroup = "MONTE_CARLO";
        public const string MiscellaneousGroup = "MISCELLANEOUS";

        // &INPUTS
        public const string FuelsAndTopographyDirectory = "FUELS_AND_TOPOGRAPHY_DIRECTORY";
        public const string WeatherDirectory = "WEATHER_DIRECTORY";

        /// <summary>Seconds each weather band covers. The driver reads it rather than assuming 3600.</summary>
        public const string DtMeteorology = "DT_METEOROLOGY";

        // &OUTPUTS
        public const string OutputsDirectory = "OUTPUTS_DIRECTORY";

        // &TIME_CONTROL
        public const string SimulationTstop = "SIMULATION_TSTOP";

        // &MONTE_CARLO

        /// <summary>
        /// Draws each case's ignition from the ignition mask instead of igniting the namelist's fixed points.
        /// Forced on per realization: a campaign whose every realization starts the same fire has nothing to
        /// average over. See <see cref="IgnitionMaskFilename"/> for what it samples.
        /// </summary>
        public const string RandomIgnitions = "RANDOM_IGNITIONS";

        /// <summary>
        /// Says the random draw is weighted by the mask raster. ELMFIRE refuses <c>RANDOM_IGNITIONS</c> without
        /// either this and <c>IGNITION_MASK_FILENAME</c> or an ignitions CSV.
        /// </summary>
        public const string UseIgnitionMask = "USE_IGNITION_MASK";

        /// <summary>
        /// The weather band each case starts in. With random ignitions ELMFIRE runs
        /// <c>NUM_ENSEMBLE_MEMBERS</c> cases <b>per band</b> in
        /// <c>METEOROLOGY_BAND_START..METEOROLOGY_BAND_STOP</c>, so the stop is pinned to the start to keep one
        /// case — one fire, one set of output rasters — per realization.
        /// </summary>
        public const string MeteorologyBandStart = "METEOROLOGY_BAND_START";

        /// <inheritdoc cref="MeteorologyBandStart"/>
        public const string MeteorologyBandStop = "METEOROLOGY_BAND_STOP";

        /// <summary>
        /// How many bands ELMFIRE reads from each weather raster. Set per realization from the bands the
        /// rasters actually carry, since the template's value describes whatever the case was built for.
        /// </summary>
        public const string NumMeteorologyTimes = "NUM_METEOROLOGY_TIMES";

        /// <summary>
        /// Seeds ELMFIRE's own Monte Carlo draws — random ignition placement out of the ignition mask, and any
        /// <c>RASTER_TO_PERTURB</c> perturbations. Varying it per realization is what makes an ensemble out of
        /// one template, and is why the campaign needs no ignition sampler of its own.
        /// </summary>
        public const string Seed = "SEED";

        /// <summary>
        /// Forced to 1 per realization. ELMFIRE can run its own ensemble internally, but it does not write a
        /// time-of-arrival raster per member, and k-PERIL needs one per realization — so the ensemble is driven
        /// from outside, one member at a time, each in its own directory.
        /// </summary>
        public const string NumEnsembleMembers = "NUM_ENSEMBLE_MEMBERS";

        // &SIMULATOR

        /// <summary>
        /// How many weather bands ELMFIRE holds in memory at once, rolling the window forward as the fire
        /// burns past it. ELMFIRE's own default is 30; at or above the case's band count it holds everything.
        /// </summary>
        /// <remarks>
        /// This used to be forced above the band count, because swapping slices crashed: a 72-band Mati run
        /// propagated correctly for 30 hours, printed
        /// <c>UPDATED WEATHER SLICE TO [30, 59]</c> and died with
        /// <c>forrtl: severe (157): Program Exception - access violation</c>. The cause was in the vendored
        /// Fortran, not the namelist: the ember routines clamped a band's number in the file at 1 and only
        /// then rebased it onto the window, so an ember still flying from a band the window had passed indexed
        /// off the front of the shared-memory window. Fixed 2026-08-05 in
        /// <c>elmfire_spotting.f90</c> and <c>elmfire_level_set.f90</c>.
        ///
        /// It is now worth using, because it is what makes a long run's memory flat: on that same case the
        /// fire is 14205.4 ac whether 80, 30, 8 or 4 bands are kept, at 814 / 421 / 218 / 180 MB peak and the
        /// same wall clock. Two is the floor - a band is interpolated against the next one.
        /// </remarks>
        public const string WxBandsKeptInMem = "WX_BANDS_KEPT_IN_MEM";

        /// <summary>
        /// The fixed ignition points, which a realization must not have: <c>elmfire_level_set.f90</c> ignites
        /// all <c>NUM_IGNITIONS</c> of them <b>in addition to</b> the random draw, so leaving them in place
        /// would put the same fire in every realization alongside the sampled one.
        /// </summary>
        public static readonly string[] FixedIgnitionKeys = { "NUM_IGNITIONS", "X_IGN", "Y_IGN", "T_IGN" };

        /// <summary>
        /// Whether a mask cell whose fuel cannot carry fire is still a candidate for a random draw. On for a
        /// single run, where it lets a deliberately placed point sit on a road; off per realization, where it
        /// would only waste draws on ground that burns nothing.
        /// </summary>
        public const string AllowNonburnablePixelIgnition = "ALLOW_NONBURNABLE_PIXEL_IGNITION";

        // &MISCELLANEOUS
        public const string PathToGdal = "PATH_TO_GDAL";
        public const string Scratch = "SCRATCH";

        /// <summary>
        /// Where <c>fuel_models.csv</c> and <c>building_fuel_models.csv</c> are read from. Written relative to
        /// the case root, so a realization running in its own directory has to be given it as an absolute path.
        /// </summary>
        public const string MiscellaneousInputsDirectory = "MISCELLANEOUS_INPUTS_DIRECTORY";

        // &INPUTS

        /// <summary>
        /// The raster the random draw is weighted by, without the extension. <c>build-case</c> always leaves one
        /// in the case's inputs — a painted ignition area if there is one, otherwise an all-ones mask — and
        /// restricts it to burnable fuel.
        /// </summary>
        public const string IgnitionMaskFilename = "IGNITION_MASK_FILENAME";
    }

    /// <summary>
    /// Generic Fortran-namelist (<c>&amp;GROUP ... /</c>) template patcher: finds or inserts a
    /// <c>KEY = value</c> line inside a named group. Mirrors the "clone a base template, patch
    /// known keys, write it out" convention already used for <c>.wui</c> files by
    /// <see cref="ProbabilisticTrigger"/>'s <c>SetKeyInSection</c> (bracket-delimited sections),
    /// adapted to ELMFIRE's <c>&amp;GROUP</c>/<c>/</c> delimiters.
    /// </summary>
    public static class ElmfireNamelist
    {
        public static string[] SetKeyInGroup(string[] lines, string group, string key, string value, bool quoted = false)
        {
            string formattedValue = quoted ? "'" + value + "'" : value;
            string newLine = " " + key + " = " + formattedValue;

            var result = new List<string>(lines);
            string groupHeader = "&" + group;
            bool inGroup = false;
            int groupStart = -1;
            int groupEnd = -1;

            for (int i = 0; i < result.Count; ++i)
            {
                string trimmed = result[i].Trim();
                if (trimmed.StartsWith("&", StringComparison.Ordinal))
                {
                    inGroup = string.Equals(trimmed, groupHeader, StringComparison.OrdinalIgnoreCase);
                    if (inGroup) groupStart = i;
                    continue;
                }
                if (inGroup && trimmed.StartsWith("/", StringComparison.Ordinal))
                {
                    groupEnd = i;
                    break;
                }
                if (inGroup)
                {
                    // All whitespace, not just spaces: real templates align their '=' with tabs
                    // (ELMFIRE's own examples do), and matching only on spaces silently misses
                    // those keys, appending a duplicate to the group instead of replacing the
                    // existing line.
                    string noSpace = StripWhitespace(trimmed);
                    if (noSpace.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        result[i] = newLine;
                        return result.ToArray();
                    }
                }
            }

            if (groupStart < 0)
            {
                //group not present in the template at all: append a new one
                result.Add("");
                result.Add(groupHeader);
                result.Add(newLine);
                result.Add("/");
            }
            else if (groupEnd < 0)
            {
                //group opened but never closed (malformed template): append the key and close it
                result.Add(newLine);
                result.Add("/");
            }
            else
            {
                result.Insert(groupEnd, newLine);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Turns a namelist that ignites at fixed points into one that draws its ignition out of the ignition
        /// mask — the difference between a single named fire and one realization of a campaign.
        /// </summary>
        /// <remarks>
        /// A fixed ignition point is right for running one fire through WUInity, and is what a scenario's
        /// <c>[IgnitionPoint]</c> means. A trigger-boundary campaign is the opposite question — where a fire
        /// could start — so its realizations have to differ in ignition, or the probability raster is one
        /// fire's boundary at probability 1 however many realizations are averaged. This is applied per
        /// realization rather than asked of the user, who would otherwise have to keep two templates in step.
        ///
        /// Three things have to agree for the draw to happen, and each fails quietly on its own:
        ///
        /// The fixed points have to go. <c>elmfire_level_set.f90</c> ignites all <c>NUM_IGNITIONS</c> of them
        /// <b>as well as</b> the sampled location, so a template left as built would burn the scenario's fire in
        /// every realization with a second, random fire beside it.
        ///
        /// <c>RANDOM_IGNITIONS</c> needs <c>USE_IGNITION_MASK</c> and <c>IGNITION_MASK_FILENAME</c>, or ELMFIRE
        /// stops. It does say so clearly, which is the exception here.
        ///
        /// The band range has to collapse to one band. With random ignitions ELMFIRE runs
        /// <c>NUM_ENSEMBLE_MEMBERS</c> cases for <i>each</i> starting weather band, and a case is a fire with its
        /// own output rasters — so a 72-band case would quietly produce 72 fires, of which a caller reading "the"
        /// output raster would get whichever it happened to glob. How much weather is read is unaffected: that is
        /// <c>NUM_METEOROLOGY_TIMES</c>, set separately.
        /// </remarks>
        /// <param name="ignitionMaskStem">
        /// The mask's filename without extension, resolved by ELMFIRE under
        /// <c>FUELS_AND_TOPOGRAPHY_DIRECTORY</c>. The caller is expected to have checked it is there.
        /// </param>
        public static string[] ForceRandomIgnition(string[] lines, string ignitionMaskStem)
        {
            const string why = "not used in a campaign; the ignition is drawn from the mask";
            foreach (string key in ElmfireNamelistKeys.FixedIgnitionKeys)
            {
                lines = CommentOutKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, key, why);
            }

            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.RandomIgnitions, ".TRUE.");
            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.UseIgnitionMask, ".TRUE.");

            //Written even when the template already names it, so the switch above can never be on with nothing
            //to read.
            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup,
                        ElmfireNamelistKeys.IgnitionMaskFilename, ignitionMaskStem, quoted: true);

            //A draw that lands on water or tarmac burns nothing, and this driver then throws the realization
            //away - so allowing it costs a realization and buys nothing. The setting exists for the opposite
            //case, a fixed point deliberately placed on a road or a rooftop, and that is precisely what a
            //campaign has just switched off; ELMFIRE only consults it when building the mask's candidate list.
            //Not redundant with build-case restricting the mask to burnable fuel: ADD_TO_IGNITION_MASK lifts
            //every cell of the mask above zero, which quietly puts the sea back in.
            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup,
                        ElmfireNamelistKeys.AllowNonburnablePixelIgnition, ".FALSE.");

            string start = GetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                               ElmfireNamelistKeys.MeteorologyBandStart);
            if (string.IsNullOrEmpty(start)
                || !int.TryParse(start, NumberStyles.Any, CultureInfo.InvariantCulture, out int band)
                || band < 1)
            {
                band = 1;
                lines = SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                            ElmfireNamelistKeys.MeteorologyBandStart, "1");
            }
            return SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                       ElmfireNamelistKeys.MeteorologyBandStop, band.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gives a realization one ignition point that the caller drew, instead of letting ELMFIRE draw it.
        /// </summary>
        /// <remarks>
        /// The opposite of <see cref="ForceRandomIgnition"/>, and used for the same purpose: a campaign whose
        /// ignition varies per realization. The difference is who samples. ELMFIRE drawing it keeps the physics
        /// in one place and is the default; the caller drawing it is necessary as soon as something else in the
        /// realization has to be <i>derived from</i> where the fire starts — aiming the wind at the community,
        /// for instance, which has to happen before the weather rasters are written.
        ///
        /// Either way the ignition is a weighted draw from the same mask (see
        /// <see cref="MaskIgnitionSampler"/>), so the ensemble it produces means the same thing.
        ///
        /// <c>RANDOM_IGNITIONS</c> goes off, which also collapses ELMFIRE's case enumeration to
        /// <c>NUM_ENSEMBLE_MEMBERS</c> — it forces <c>IWX_BAND_STOP = IWX_BAND_START</c> itself in that branch,
        /// so the band range needs no adjusting here. <c>USE_IGNITION_MASK</c> goes off too: nothing reads the
        /// mask any more, and leaving it on would have ELMFIRE load a raster to ignore.
        /// </remarks>
        public static string[] SetSampledIgnition(string[] lines, double x, double y, double timeSeconds = 0.0)
        {
            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.RandomIgnitions, ".FALSE.");
            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.UseIgnitionMask, ".FALSE.");

            //Any fixed points the template carried are replaced, not added to: elmfire_level_set.f90 ignites
            //every one of NUM_IGNITIONS, so a template with its own point would burn two fires per realization.
            lines = CommentOutKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, "X_IGN",
                        "replaced by this realization's drawn ignition");
            lines = CommentOutKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, "Y_IGN",
                        "replaced by this realization's drawn ignition");
            lines = CommentOutKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, "T_IGN",
                        "replaced by this realization's drawn ignition");

            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, "NUM_IGNITIONS", "1");
            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, "X_IGN(1)",
                        x.ToString("0.##", CultureInfo.InvariantCulture));
            lines = SetKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, "Y_IGN(1)",
                        y.ToString("0.##", CultureInfo.InvariantCulture));
            return SetKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup, "T_IGN(1)",
                       timeSeconds.ToString("0.##", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Comments out every assignment to <paramref name="key"/> inside <paramref name="group"/>, keeping the
        /// original line after <paramref name="why"/> so the realization's namelist still records what the
        /// template asked for and why it is not in force.
        /// </summary>
        /// <remarks>
        /// Subscripts are matched too — <c>X_IGN(1)</c> is an assignment to <c>X_IGN</c>, and the fixed
        /// ignitions this exists to remove are always written that way.
        ///
        /// Commented rather than deleted because these files are read by hand when a realization misbehaves,
        /// and a key that silently vanished between the template and the run is the hardest kind of difference
        /// to notice.
        /// </remarks>
        public static string[] CommentOutKeyInGroup(string[] lines, string group, string key, string why)
        {
            var result = new List<string>(lines);
            string groupHeader = "&" + group;
            bool inGroup = false;

            for (int i = 0; i < result.Count; ++i)
            {
                string trimmed = result[i].Trim();
                if (trimmed.StartsWith("&", StringComparison.Ordinal))
                {
                    inGroup = string.Equals(trimmed, groupHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inGroup && trimmed.StartsWith("/", StringComparison.Ordinal)) break;
                if (!inGroup || trimmed.StartsWith("!", StringComparison.Ordinal)) continue;

                string noSpace = StripWhitespace(trimmed);
                if (noSpace.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                    || noSpace.StartsWith(key + "(", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = "! " + why + ": " + trimmed;
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// The value assigned to <paramref name="key"/> inside <paramref name="group"/>, or null when the group
        /// does not set it. Quotes are stripped; a commented line does not count as set.
        /// </summary>
        public static string GetKeyInGroup(string[] lines, string group, string key)
        {
            string groupHeader = "&" + group;
            bool inGroup = false;

            foreach (string raw in lines)
            {
                string trimmed = raw.Trim();
                if (trimmed.StartsWith("&", StringComparison.Ordinal))
                {
                    inGroup = string.Equals(trimmed, groupHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inGroup && trimmed.StartsWith("/", StringComparison.Ordinal)) break;
                if (!inGroup || trimmed.StartsWith("!", StringComparison.Ordinal)) continue;

                if (!StripWhitespace(trimmed).StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;

                string value = trimmed.Substring(trimmed.IndexOf('=') + 1).Trim();

                //A trailing inline comment is part of the line, not of the value.
                int bang = value.IndexOf('!');
                if (bang >= 0) value = value.Substring(0, bang).Trim();

                return value.Trim('\'', '"');
            }

            return null;
        }

        private static string StripWhitespace(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
