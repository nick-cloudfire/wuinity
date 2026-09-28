using PREACT.Math;
using System;
using System.Threading.Tasks;
using System.IO;

namespace PREACT.Tools
{
    public static class OpenMeteoDownloader
    {
        private static OpenMeteo.OpenMeteoClient _historicalClient = new OpenMeteo.OpenMeteoClient(true);
        private static OpenMeteo.OpenMeteoClient _forecastClient = new OpenMeteo.OpenMeteoClient(false);
        //"temperature_2m", "relative_humidity_2m", "precipitation", "wind_speed_10m", "wind_direction_10m", "cloud_cover", "direct_radiation", "boundary_layer_height" 
        static readonly OpenMeteo.HourlyOptionsParameter[] _parameters = { OpenMeteo.HourlyOptionsParameter.temperature_2m, OpenMeteo.HourlyOptionsParameter.relativehumidity_2m, OpenMeteo.HourlyOptionsParameter.precipitation,
            OpenMeteo.HourlyOptionsParameter.windspeed_10m, OpenMeteo.HourlyOptionsParameter.winddirection_10m, OpenMeteo.HourlyOptionsParameter.cloudcover, OpenMeteo.HourlyOptionsParameter.direct_radiation, OpenMeteo.HourlyOptionsParameter.boundary_layer_height};

        /// <summary>How far the ERA5 reanalysis archive trails real time; requests past this return nothing.</summary>
        private const int ArchiveLagDays = 6;

        private static string F(float? v)
        {
            return v.HasValue ? v.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string Iso(DateTime d)
        {
            return d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static async Task Download(Vector2d latLon, DateTime start, DateTime end, string newFilePath)
        {
            Engine.Message(null, Engine.LogType.Log, "Starting attempt to dowload weather data.");

            bool forecast = false;
            OpenMeteo.OpenMeteoClient _client = _historicalClient;
            if (DateTime.Compare(end, DateTime.Now) > 0)
            {
                if((end - DateTime.Now).Days < 16)
                {
                    _client = _forecastClient;
                    //Was never set, so a forecast request still asked for the whole calendar year
                    //below - a range the forecast endpoint cannot serve, and the query came back
                    //empty. This is what made any present or future dated scenario fail.
                    forecast = true;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Log, "Open-meteo only provides 16 days of forecasting, unable to download weather for specified dates.");
                    return;
                }
            }

            //set options to download
            OpenMeteo.WeatherForecastOptions options = new OpenMeteo.WeatherForecastOptions((float)latLon.x, (float)latLon.y);
            options.Windspeed_Unit = OpenMeteo.WindspeedUnitType.ms;
            //canadian FBP needs all year data for FWI/BUI/FFMC etc
            if(forecast)
            {
                //Zero-padded ISO: Open-Meteo wants YYYY-MM-DD, and the previous interpolation
                //produced "2026-7-3" for single-digit months and days.
                options.Start_date = Iso(start);
                options.End_date = Iso(end);
            }
            else
            {
                //The reanalysis archive trails real time by several days, so asking for the rest
                //of the current year returns nothing at all rather than the part that does exist.
                //Clamped to the last day the archive can be expected to hold.
                DateTime archiveEnd = DateTime.Now.Date.AddDays(-ArchiveLagDays);
                DateTime requestedEnd = new DateTime(end.Year, 12, 31);
                if (requestedEnd > archiveEnd) requestedEnd = archiveEnd;

                DateTime requestedStart = new DateTime(start.Year, 1, 1);
                if (requestedStart > requestedEnd)
                {
                    Engine.Message(null, Engine.LogType.SimulationError,
                        $"The historical archive only reaches {archiveEnd:yyyy-MM-dd}, which is before the requested " +
                        $"start of {requestedStart:yyyy-MM-dd}. Pick an earlier date for the scenario.");
                    return;
                }

                if (requestedEnd < new DateTime(end.Year, 12, 31))
                {
                    Engine.Message(null, Engine.LogType.Log,
                        $"Historical weather is only available to {requestedEnd:yyyy-MM-dd}; requesting up to there.");
                }

                options.Start_date = Iso(requestedStart);
                options.End_date = Iso(requestedEnd);
            }
            options.Hourly.Add(_parameters);

            OpenMeteo.WeatherForecast? weatherStream = await _client.QueryAsync(options);
            if (weatherStream != null)
            {
                Engine.Message(null, Engine.LogType.Log, "Downloaded weather data, saving to disk.");

                //Written to a temporary file and moved into place, so a reader never sees a half-written archive -
                //which is what concurrent campaign realizations used to race on.
                string temp = newFilePath + ".download.tmp";
                using (StreamWriter file = new StreamWriter(temp))
                {
                    file.WriteLine($"Latitide,{weatherStream.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    file.WriteLine($"Longitude,{weatherStream.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    file.WriteLine($"Elevation,{weatherStream.Elevation.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

                    if (weatherStream.Hourly != null)
                    {
                        OpenMeteo.Hourly hourly = weatherStream.Hourly;
                        if (hourly.Time != null)
                        {
                            //header, ending in the format marker ClimatologySampler checks before trusting the
                            //fire weather columns
                            file.WriteLine($"{nameof(hourly.Time)},{nameof(hourly.Temperature_2m)} [{weatherStream.HourlyUnits.Temperature_2m}],{nameof(hourly.Relativehumidity_2m)} [{weatherStream.HourlyUnits.Relativehumidity_2m}],{nameof(hourly.Precipitation)} [{weatherStream.HourlyUnits.Precipitation}]," +
                                $"{nameof(hourly.Windspeed_10m)} [{weatherStream.HourlyUnits.Windspeed_10m}],{nameof(hourly.Winddirection_10m)} [{weatherStream.HourlyUnits.Winddirection_10m}],{nameof(hourly.Cloudcover)} [{weatherStream.HourlyUnits.Cloudcover}]," +
                                $"{nameof(hourly.Direct_radiation)} [{weatherStream.HourlyUnits.Direct_radiation}],{nameof(hourly.Boundary_layer_height)} [{weatherStream.HourlyUnits.Boundary_layer_height}],FFMC hourly [-],FFMC [-],DMC [-],DC [-],ISI [-],BUI [-],FWI [-]"
                                + Utility.ClimatologySampler.FormatHeaderSuffix(weatherStream.Longitude));

                            //The fire weather codes from km/h wind and the 24 h rain at 12:00 local standard time,
                            //by the derivation the re-derivation of old archives uses too. They were computed from
                            //m/s wind and one hour of rain at 12:00 UTC, which kept the Drought Code accumulating
                            //through every winter and ranked the campaign's candidate days by drought, not wind.
                            var raw = new System.Collections.Generic.List<Utility.ClimatologySampler.RawHour>(hourly.Time.Length);
                            for (int i = 0; i < hourly.Time.Length; i++)
                            {
                                DateTime.TryParse(hourly.Time[i], System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.None, out DateTime dateTime);
                                raw.Add(new Utility.ClimatologySampler.RawHour
                                {
                                    Time = dateTime,
                                    Temperature = hourly.Temperature_2m[i] ?? 0,
                                    RelativeHumidity = hourly.Relativehumidity_2m[i] ?? 0,
                                    Precipitation = hourly.Precipitation[i] ?? 0,
                                    WindSpeedMps = hourly.Windspeed_10m[i] ?? 0,
                                });
                            }

                            Utility.ClimatologySampler.DerivedCodes[] codes =
                                Utility.ClimatologySampler.DeriveFireWeatherCodes(raw, weatherStream.Longitude);

                            for (int i = 0; i < hourly.Time.Length; i++)
                            {
                                file.WriteLine(string.Join(",",
                                    hourly.Time[i], F(hourly.Temperature_2m[i]), F(hourly.Relativehumidity_2m[i]),
                                    F(hourly.Precipitation[i]), F(hourly.Windspeed_10m[i]), F(hourly.Winddirection_10m[i]),
                                    F(hourly.Cloudcover[i]), F(hourly.Direct_radiation[i]), F(hourly.Boundary_layer_height[i]))
                                    + "," + Utility.ClimatologySampler.FormatCodes(codes[i]));
                            }
                        }
                    }
                }

                File.Copy(temp, newFilePath, overwrite: true);
                File.Delete(temp);
                Engine.Message(null, Engine.LogType.Log, $"Saved weather data to {newFilePath}.");
            }
            else
            {
                //Names the request that came back empty. "Failed to download weather data" alone
                //gave no way to tell an unreachable service from an out-of-range date range.
                Engine.Message(null, Engine.LogType.SimulationError,
                    $"{nameof(OpenMeteoDownloader)} got no data for {options.Start_date} to {options.End_date} at " +
                    $"{latLon.x:F4},{latLon.y:F4} from the {(forecast ? "forecast" : "historical archive")} endpoint. " +
                    "Check the scenario dates are within range and that the service is reachable.");
            }
        }
    }
}
