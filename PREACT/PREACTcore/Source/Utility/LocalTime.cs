//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;

namespace PREACT.Utility
{
    /// <summary>
    /// The civil time zone a scenario's dates are stated in, and the conversions between it and the UTC the
    /// weather record is on.
    /// </summary>
    /// <remarks>
    /// A scenario's <c>StartDateTime</c> is local civil time at the domain, daylight saving included; the ERA5 archive
    /// and every Open-Meteo weather CSV are hourly UTC. The simulation clock (<see cref="TimeManager"/>), the case
    /// weather's band schedule and the run's weather report all convert through here, with the zone looked up at the
    /// domain's south-west corner, so the three agree on which hour of the record "13:00" is. The case weather used
    /// to take the local start hour as a UTC hour: a 13:00 start read 16:00 at Mati in summer and 06:00 in California.
    /// </remarks>
    public static class LocalTime
    {
        /// <summary>
        /// The civil time zone at a point, from its coordinates (GeoTimeZone), as the host's time zone database knows
        /// it. When the database has no entry for it, a fixed zone of the point's mean solar offset (15 degrees an
        /// hour, no daylight saving) stands in, so the conversion is at most an hour or two out rather than failing.
        /// </summary>
        public static TimeZoneInfo ZoneAt(double latitude, double longitude)
        {
            string iana = null;
            try
            {
                iana = GeoTimeZone.TimeZoneLookup.GetTimeZone(latitude, longitude).Result;
            }
            catch (Exception)
            {
            }

            if (!string.IsNullOrEmpty(iana))
            {
                //Windows' own zone names on Windows (and in Unity's Mono there); .NET elsewhere accepts either.
                foreach (Func<string> id in new Func<string>[] { () => TimeZoneConverter.TZConvert.IanaToWindows(iana), () => iana })
                {
                    try
                    {
                        return TimeZoneInfo.FindSystemTimeZoneById(id());
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            int hours = (int)System.Math.Round(longitude / 15.0);
            return TimeZoneInfo.CreateCustomTimeZone("Mean solar " + hours.ToString("+00;-00", System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.FromHours(hours), "UTC" + hours.ToString("+00;-00", System.Globalization.CultureInfo.InvariantCulture), "UTC");
        }

        /// <summary>
        /// The UTC instant a local civil time at <paramref name="zone"/> stands for. A time that does not exist (inside
        /// a spring-forward gap) is taken an hour later, and one that occurs twice (autumn) as its first occurrence.
        /// </summary>
        public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
        {
            DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(unspecified))
            {
                unspecified = unspecified.AddHours(1);
            }

            TimeSpan offset = zone.IsAmbiguousTime(unspecified)
                ? MaxOffset(zone.GetAmbiguousTimeOffsets(unspecified))
                : zone.GetUtcOffset(unspecified);
            return DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Unspecified);
        }

        /// <summary>The local civil time at <paramref name="zone"/> of a UTC instant.</summary>
        public static DateTime FromUtc(DateTime utc, TimeZoneInfo zone)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        }

        /// <summary>The daylight offset of an ambiguous time - its first occurrence - which is the larger one.</summary>
        private static TimeSpan MaxOffset(TimeSpan[] offsets)
        {
            TimeSpan best = offsets[0];
            foreach (TimeSpan o in offsets)
            {
                if (o > best) best = o;
            }
            return best;
        }
    }
}
