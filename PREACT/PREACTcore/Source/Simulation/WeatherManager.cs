using PREACT.Math;
using System;
using PREACT.Weather;
using System.IO;
using System.Collections.Generic;

namespace PREACT
{    
    public class WeatherManager
    {
        private Simulation _simulation;
        private Wildfire.FireWeatherIndex _fwi;
        private bool _fwiNeedsUpdate = true;
        private Wildfire.HourlyFFMC _ffmcHourly;
        private DateTime _lastDateTime;
        private WeatherStream _weatherData;

        //values that are needed for something else
        int _hoursSinceRain;
        double _totalRainToday, _totalRainYesterday, _totalRain2DaysAgo, _totalRainSimulation;
        double _maxTemperatureToday, _maxTemperatureYesterday;
        DailyKBDI _DailyKBDI;

        private float _weatherReferenceElevation;

        private const float _inverseSecondsPerHour = 1.0f / 3600.0f;

        //need current values and upcoming values for interpolation, assuming hourly input
        /*private float _currentTemperature, _nextTemperature, _interpolatedTemperature;
        private float _currentRelativeHumidity, _nextRelativeHumidity, _interpolatedRelativeHumidity;
        private float _currentPrecipitation, _nextPrecipitation, _interpolatedPrecipitation;
        private float _currentWindSpeed, _nextWindSpeed, _interpolatedWindSpeed;
        private float _currentWindDirection, _nextWindDirection, _interpolatedWindDirection;
        private float _currentCloudCover, _nextCloudCover, _interpolatedCloudCover;*/

        private HourlyWeather _currentHourlyData, _nextHourlyData, _interpolatedHourlyData;


        //"temperature_2m", "relative_humidity_2m", "precipitation", "wind_speed_10m", "wind_direction_10m", "cloud_cover", "direct_radiation", "boundary_layer_height" 
        readonly OpenMeteo.HourlyOptionsParameter[] _parameters = { OpenMeteo.HourlyOptionsParameter.temperature_2m, OpenMeteo.HourlyOptionsParameter.relativehumidity_2m, OpenMeteo.HourlyOptionsParameter.precipitation,
            OpenMeteo.HourlyOptionsParameter.windspeed_10m, OpenMeteo.HourlyOptionsParameter.winddirection_10m, OpenMeteo.HourlyOptionsParameter.cloudcover, OpenMeteo.HourlyOptionsParameter.direct_radiation, OpenMeteo.HourlyOptionsParameter.boundary_layer_height};

        public Wildfire.FireWeatherIndex FWI { get => _fwi; }
        public double FFMCHourly { get => _ffmcHourly.Value; }
        public int HoursSinceRain { get => _hoursSinceRain; }
        public double KBDI { get => _DailyKBDI.KBDI; }


        public WeatherManager(Simulation simulation, TimeManager time)
        {
            _simulation = simulation;

            //From the weather section, which is where these belong: they are indices computed from the
            //weather series and carried forward, whatever fire module is running. They used to be read off
            //the cell-based spread model's settings, so they were only ever set for a scenario using that
            //module and silently took their defaults for every other one - including the mean annual
            //precipitation, which was not read at all and hardcoded to 1500 here.
            Input.WeatherInput weatherInput = simulation.Input.Weather;
            _fwi = new Wildfire.FireWeatherIndex(weatherInput.StartFFMC, weatherInput.StartDMC, weatherInput.StartDC);
            _ffmcHourly = new Wildfire.HourlyFFMC(weatherInput.StartHourlyFFMC);
            _DailyKBDI = new DailyKBDI(weatherInput.StartKBDI, weatherInput.MeanAnnualPrcp);

            //Runtime copies: a run must not change the scenario the GUI edits and saves (the ELMFIRE rebase used
            //to write the case's archive and anchor back into the input).
            _weatherFile = simulation.Input.Weather.WeatherFile;
            _anchor = simulation.Input.Weather.WeatherAnchorDateTime;

            //Before the weather is loaded, because the offset decides which span of the record has to be
            //covered - and so whether the file on disk is usable at all.
            SetAnchor(_anchor, time.StartDateTime);

            LoadOrDownloadWeather(time);
            Update(time.StartDateTime, true);
        }

        private string _weatherFile;
        private DateTime _anchor;

        /// <summary>Whether any weather was loaded. Without it every reported value is 0 and the indices stay at their seeds.</summary>
        public bool HasWeather { get => _weatherData != null; }

        /// <summary>The weather file this run reads (scenario-relative), which an ELMFIRE run may have switched to its case's archive.</summary>
        public string WeatherFile { get => _weatherFile; }

        /// <summary>The instant in the record the simulation start reads from, for this run; default when unanchored.</summary>
        public DateTime WeatherAnchor { get => _anchor; }

        /// <summary>Whether this run's weather is anchored to a day other than the simulation's own.</summary>
        public bool HasWeatherAnchor { get => _anchor != default; }

        /// <summary>
        /// How far the weather record is offset from the simulation's own clock.
        /// </summary>
        /// <remarks>
        /// Zero for a scenario that reads weather at its own dates. Non-zero when the fire was computed against
        /// a historical day drawn out of the record — see <see cref="Input.WeatherInput.WeatherAnchorDateTime"/>.
        /// </remarks>
        private TimeSpan _weatherOffset = TimeSpan.Zero;

        /// <summary>The instant in the weather record corresponding to a simulation time.</summary>
        private DateTime WeatherTime(DateTime simulationTime)
        {
            return simulationTime + _weatherOffset;
        }

        private void SetAnchor(DateTime anchor, DateTime simulationStart)
        {
            _weatherOffset = anchor == default ? TimeSpan.Zero : anchor - simulationStart;
        }

        /// <summary>
        /// Points the weather at a different span of the record, once something else has established which
        /// day the run is actually about.
        /// </summary>
        /// <remarks>
        /// Needed because the ELMFIRE module is created <em>after</em> this manager exists, and building a case
        /// is what draws the historical day. Without this, a scenario that builds its case as part of the run
        /// would report weather from its own calendar date on that run and the sampled day's only on the next —
        /// the same scenario giving two different answers depending on whether the case already existed.
        ///
        /// <paramref name="weatherFile"/> is scenario-relative and may be null to keep the current file. It is
        /// reloaded when it differs, since the case's own ERA5 archive spans decades where a scenario's weather
        /// CSV usually spans one year and would not contain the sampled day at all.
        ///
        /// Note what this does <b>not</b> do: the drought codes are not re-marched over the record before the
        /// sampled day, because no antecedent marching exists — <c>Initialize</c> has been commented out since
        /// before this change. DMC and DC therefore still begin at their <c>[Weather]</c> seeds. What this fixes
        /// is that temperature, humidity, wind, precipitation and the hourly FFMC now come from the hours the
        /// fire was actually computed against.
        /// </remarks>
        public void Rebase(DateTime anchor, string weatherFile, TimeManager time)
        {
            if (anchor == default)
            {
                return;
            }

            SetAnchor(anchor, time.StartDateTime);
            _anchor = anchor;

            bool reload = !string.IsNullOrEmpty(weatherFile)
                          && !string.Equals(weatherFile, _weatherFile, StringComparison.OrdinalIgnoreCase);

            if (reload)
            {
                _weatherFile = weatherFile;
            }

            if (reload || _weatherData == null)
            {
                LoadOrDownloadWeather(time);
            }

            //Re-read the current hour through the new offset, so the first reported values are already the
            //sampled day's rather than the previous anchor's. Only the hour: a forced Update would step the
            //daily drought code a second time for the same day.
            ReadHour(time.StartDateTime);

            Engine.Message(_simulation, Engine.LogType.Log,
                $"Weather rebased onto {anchor:yyyy-MM-dd HH:mm} from the record ("
                + $"offset {_weatherOffset.TotalDays:F0} days), so the conditions reported match the day the "
                + "fire was computed against.");
        }

        /// <summary>
        /// Whether a weather file spans the part of the record this run reads.
        /// </summary>
        /// <remarks>
        /// The <b>offset</b> span, not the simulation's own dates. With an anchor set, a scenario dated 2020 may
        /// be reading 2001 out of the archive, and testing the raw simulation range would reject the very file
        /// that contains the right weather — then fall through to downloading a year the run never looks at.
        /// </remarks>
        private bool Covers(WeatherStream stream, TimeManager timeManager)
        {
            DateTime first = WeatherTime(timeManager.StartDateTime);
            DateTime last = WeatherTime(timeManager.EndDateTime);

            return DateTime.Compare(first, stream.FirstEntry) >= 0
                   && DateTime.Compare(last, stream.LastEntry) <= 0;
        }

        public void Update(DateTime currentDateTime, bool forceUpdate = false)
        {
            if (_weatherData == null)
            {
                //No weather configured, or none could be had: nothing to report, nothing to march forward.
                _lastDateTime = currentDateTime;
                return;
            }

            bool newMinute = _lastDateTime.Minute != currentDateTime.Minute;
            bool newHour = _lastDateTime.Hour != currentDateTime.Hour;
            bool newDay = _lastDateTime.DayOfYear != currentDateTime.DayOfYear;
            if(forceUpdate)
            {
                newMinute = true;
                newHour = true;
                newDay = true;
            }

            if (newDay)
            {
                _fwiNeedsUpdate = true;

                _maxTemperatureYesterday = _maxTemperatureToday;
                _maxTemperatureToday = -300.0;

                _totalRain2DaysAgo = _totalRainYesterday;
                _totalRainYesterday = _totalRainToday;
                _totalRainToday = 0;

                _DailyKBDI.CalculateDailyKBDI_Metric(_maxTemperatureYesterday, _totalRainYesterday);
            }

            if (newHour)
            {
                //read new values from weather input stream, at the moment in the record this simulation time
                //corresponds to - the same instant the fire's own weather rasters were written from.
                ReadHour(currentDateTime);

                //now update hourly values
                _ffmcHourly.Calculate(_currentHourlyData._temp, _currentHourlyData._rh, _currentHourlyData._windSpeed * 3.6, _currentHourlyData._precip);                          
                
                if(_currentHourlyData._temp > _maxTemperatureToday)
                {
                    _maxTemperatureToday = _currentHourlyData._temp;
                }

                _hoursSinceRain = _currentHourlyData._precip > 0 ? 0 : _hoursSinceRain + 1;
                _totalRainToday += _currentHourlyData._precip;
                _totalRainSimulation += _currentHourlyData._precip;
            }
            else
            {
                //update all relevant data
                float timeFraction = (currentDateTime.Minute * 60 + currentDateTime.Second) * _inverseSecondsPerHour;
                HourlyWeather.InterpolateData(_currentHourlyData, _nextHourlyData, timeFraction, ref _interpolatedHourlyData);
            }

            if (newMinute)
            {

            }

            //The record's date, not the scenario's: the FWI's day-length factor is seasonal, so a fire computed
            //against an August day must be indexed as August even when the scenario is dated in July.
            if (_fwiNeedsUpdate && currentDateTime.Hour == 12)
            {
                _fwiNeedsUpdate = false;
                _fwi.CalculateDay(WeatherTime(currentDateTime), _currentHourlyData._temp, _currentHourlyData._rh,
                    _currentHourlyData._windSpeed * 3.6, _currentHourlyData._precip);
            }

            //lastly just update DateTime
            _lastDateTime = currentDateTime;
        }

        private void ReadHour(DateTime currentDateTime)
        {
            if (_weatherData == null)
            {
                return;
            }
            _weatherData.GetHourlyData(WeatherTime(currentDateTime), out _currentHourlyData, out _nextHourlyData);
            _interpolatedHourlyData = _currentHourlyData;
        }

        /// <summary>
        /// Loads the weather this run reads: the scenario's file, else the downloader's default name, else the
        /// shared cache - whichever covers the run. Downloads a year from Open-Meteo only when the scenario asked
        /// for weather (a WeatherFile is set) and nothing on disk covers the run.
        /// </summary>
        /// <remarks>
        /// A scenario without a weather file used to download a full year from Open-Meteo on every run, silently.
        /// The weather is display-only - neither the evacuation nor the trigger boundary reads it - so with no
        /// weather configured the run simply reports none.
        /// </remarks>
        private void LoadOrDownloadWeather(TimeManager timeManager)
        {
            _weatherData = null;
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_weatherFile))
            {
                candidates.Add(Input.PREACTInput.ResolvePath(_simulation.Input.RootFolder, _weatherFile));
            }
            //the default name the downloader saves under, and the shared cache
            candidates.Add(Path.Combine(_simulation.Input.RootFolder, $"{_simulation.Input.Simulation.Name}_weather.csv"));
            candidates.Add(SharedWeatherCachePath());

            foreach (string filePath in candidates)
            {
                if (!File.Exists(filePath))
                {
                    continue;
                }

                WeatherStream wD = WeatherStream.LoadFromFile(filePath, out bool success);
                if (!success)
                {
                    continue;
                }

                Engine.Message(_simulation, Engine.LogType.Log, $"Weather data in {Path.GetFileName(filePath)} spans {wD.FirstEntry:yyyy-MM-dd HH:mm} to {wD.LastEntry:yyyy-MM-dd HH:mm}.");
                if (Covers(wD, timeManager))
                {
                    _weatherData = wD;
                    _weatherReferenceElevation = (float)wD.Elevation;
                    return;
                }

                Engine.Message(_simulation, Engine.LogType.Log, $"{Path.GetFileName(filePath)} does not cover the part of the record this run reads "
                    + $"({WeatherTime(timeManager.StartDateTime):yyyy-MM-dd HH:mm} to {WeatherTime(timeManager.EndDateTime):yyyy-MM-dd HH:mm}).");
            }

            if (string.IsNullOrWhiteSpace(_weatherFile))
            {
                Engine.Message(_simulation, Engine.LogType.Log, "No weather file is set, so no weather is reported for this run (nothing is downloaded).");
                return;
            }

            Engine.Message(_simulation, Engine.LogType.Warning, $"The weather file {_weatherFile} was not found or does not cover this run; "
                + "downloading the years needed from Open-Meteo.");

            //A download failure used to take the whole process down with it. The weather is only reported, so a
            //failed download is a warning, not a reason to stop the run.
            try
            {
                DownloadWeather();
            }
            catch (Exception e)
            {
                _weatherData = null;
                Engine.Message(_simulation, Engine.LogType.Warning, $"Weather download failed ({e.Message}); no weather is reported for this run.");
            }
        }

        /// <summary>
        /// Where a downloaded year of weather is cached for reuse. Keyed on the location and year
        /// range it actually covers rather than on the simulation name, so every realization of a
        /// campaign - each of which runs under its own generated name - shares one file instead of
        /// fetching its own identical copy.
        /// </summary>
        private string SharedWeatherCachePath()
        {
            double lat = _simulation.Spatial.SimulationCenterLatLon.x;
            double lon = _simulation.Spatial.SimulationCenterLatLon.y;

            //The anchored years, matching what DownloadWeather fetches. Keying on the scenario's own years
            //while the file holds the anchored ones would make the name a lie, and two runs with different
            //anchors would fight over one cache entry.
            int firstYear = WeatherTime(_simulation.Time.StartDateTime).Year;
            int lastYear = WeatherTime(_simulation.Time.EndDateTime).Year;

            string name = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "weather_{0:F4}_{1:F4}_{2}_{3}.csv", lat, lon, firstYear, lastYear)
                .Replace('-', 'm'); //keep negative lat/lon out of the filename as a leading dash

            return Path.Combine(_simulation.Input.RootFolder, name);
        }

        private void DownloadWeather()
        {
            //if we do not have weather file/file is not complete for period we try to stream it in.
            OpenMeteo.OpenMeteoClient client = new OpenMeteo.OpenMeteoClient(true);
            //set options to download
            OpenMeteo.WeatherForecastOptions options = new OpenMeteo.WeatherForecastOptions((float)_simulation.Spatial.SimulationCenterLatLon.x, (float)_simulation.Spatial.SimulationCenterLatLon.y);
            options.Windspeed_Unit = OpenMeteo.WindspeedUnitType.ms;
            //canadian FBP needs all year data for FWI/BUI/FFMC etc
            //The years the run actually reads, which with a weather anchor are not the scenario's own: an
            //anchored run looks up a historical day out of the record, and downloading the scenario's calendar
            //year would fetch a year nothing ever reads and then still not have the day it needs.
            options.Start_date = new string($"{WeatherTime(_simulation.Time.StartDateTime).Year}-01-01");
            options.End_date = new string($"{WeatherTime(_simulation.Time.EndDateTime).Year}-12-31");
            options.Hourly.Add(_parameters);

            //do query
            OpenMeteo.WeatherForecast? weatherStream = client.Query(options);
            if (weatherStream != null)
            {
                _weatherReferenceElevation = weatherStream.Elevation;

                //Written to a private temporary file and then moved into place, because several
                //realizations may reach this at once: a reader must never see a half-written cache,
                //and a download that dies midway must not leave a truncated one behind. The move is
                //within the same directory, so it is atomic enough for that.
                string filePath = SharedWeatherCachePath();
                string tempPath = filePath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                using (StreamWriter file = new StreamWriter(tempPath))
                {
                    file.WriteLine(FormattableString.Invariant($"Latitide,{weatherStream.Latitude}"));
                    file.WriteLine(FormattableString.Invariant($"Longitude,{weatherStream.Longitude}"));
                    file.WriteLine(FormattableString.Invariant($"Elevation,{weatherStream.Elevation}"));

                    if (weatherStream.Hourly != null)
                    {
                        OpenMeteo.Hourly hourly = weatherStream.Hourly;
                        if (hourly.Time != null)
                        {
                            //header
                            file.WriteLine($"{nameof(hourly.Time)},{nameof(hourly.Temperature_2m)} [{weatherStream.HourlyUnits.Temperature_2m}],{nameof(hourly.Relativehumidity_2m)} [{weatherStream.HourlyUnits.Relativehumidity_2m}],{nameof(hourly.Precipitation)} [{weatherStream.HourlyUnits.Precipitation}]," +
                                $"{nameof(hourly.Windspeed_10m)} [{weatherStream.HourlyUnits.Windspeed_10m}],{nameof(hourly.Winddirection_10m)} [{weatherStream.HourlyUnits.Winddirection_10m}],{nameof(hourly.Cloudcover)} [{weatherStream.HourlyUnits.Cloudcover}]," +
                                $"{nameof(hourly.Direct_radiation)} [{weatherStream.HourlyUnits.Direct_radiation}],{nameof(hourly.Boundary_layer_height)} [{weatherStream.HourlyUnits.Boundary_layer_height}],FFMC hourly [-],FFMC [-],DMC [-],DC [-],ISI [-],BUI [-],FWI [-]");

                            //save actual data to usable format in memory
                            HourlyWeather[] hourlyArray = new HourlyWeather[hourly.Time.Length];
                            DateTime startDateTime, endDateTime;
                            DateTime.TryParse(hourly.Time[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out startDateTime);
                            DateTime.TryParse(hourly.Time[hourly.Time.Length - 1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out endDateTime);
                            _weatherData = new WeatherStream(weatherStream.Latitude, weatherStream.Longitude, weatherStream.Elevation, startDateTime, endDateTime, hourlyArray);

                            Wildfire.FireWeatherIndex fwi = new Wildfire.FireWeatherIndex();
                            Wildfire.HourlyFFMC ffmcHourly = new Wildfire.HourlyFFMC();

                            //loop through all data
                            for (int i = 0; i < hourly.Time.Length; i++)
                            {
                                bool timeParsed = DateTime.TryParse(hourly.Time[i], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dateTime);
                                //The indices take wind in km/h; the download is in m/s. The cache's FFMC/FWI columns
                                //used to be computed with m/s (3.6 times too little wind) while Update, reading the same
                                //functions, converted - so the file and the run disagreed.
                                double windKmh = (hourly.Windspeed_10m[i] ?? 0) * 3.6;
                                if(timeParsed && dateTime.Hour == 12)
                                {
                                    fwi.CalculateDay(dateTime, hourly.Temperature_2m[i] ?? 0, hourly.Relativehumidity_2m[i] ?? 0, windKmh, hourly.Precipitation[i] ?? 0);
                                }
                                ffmcHourly.Calculate(hourly.Temperature_2m[i] ?? 0, hourly.Relativehumidity_2m[i] ?? 0, windKmh, hourly.Precipitation[i] ?? 0);

                                file.WriteLine(FormattableString.Invariant($"{hourly.Time[i]},{hourly.Temperature_2m[i]},{hourly.Relativehumidity_2m[i]},{hourly.Precipitation[i]},{hourly.Windspeed_10m[i]},{hourly.Winddirection_10m[i]},{hourly.Cloudcover[i]},{hourly.Direct_radiation[i]},{hourly.Boundary_layer_height[i]},{ffmcHourly.Value},{fwi.FFMC},{fwi.DMC},{fwi.DC},{fwi.ISI},{fwi.BUI},{fwi.FWI}"));

                                _weatherData.HourlyData[i] = new HourlyWeather(hourly.Temperature_2m[i] ?? 0, hourly.Relativehumidity_2m[i] ?? 0,hourly.Precipitation[i] ?? 0, hourly.Windspeed_10m[i] ?? 0, hourly.Winddirection_10m[i] ?? 0, hourly.Cloudcover[i] ?? 0, hourly.Direct_radiation[i] ?? 0, hourly.Boundary_layer_height[i] ?? 0);
                            }
                        }
                    }
                }

                PublishWeatherCache(tempPath, filePath);
            }
            else
            {
                Engine.Message(_simulation, Engine.LogType.Warning, "Could not download the weather; no weather is reported for this run.");
            }
        }

        /// <summary>
        /// Moves a freshly-written cache into place. A concurrent realization may have finished
        /// the same download first; that is a benign race - the two files hold the same weather -
        /// so losing it just means discarding this copy rather than failing the run.
        /// </summary>
        private void PublishWeatherCache(string tempPath, string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(tempPath);
                    return;
                }

                File.Move(tempPath, filePath);
            }
            catch (Exception e)
            {
                //The weather itself is already in memory, so this only costs the next realization
                //a re-download - not worth failing over.
                Engine.Message(_simulation, Engine.LogType.Log, $"Could not publish the weather cache: {e.Message}");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        public float GetTemperature()
        {
            return _interpolatedHourlyData._temp;
        }

        public float GetTemperature(Vector3d simulationPosition, float elevation)
        {           
            return _interpolatedHourlyData._temp + 0.0065f * (_weatherReferenceElevation - elevation); //6.5 deg C / 1000 meters lapse rate https://en.wikipedia.org/wiki/Lapse_rate
        }

        public float GetRelativeHumidity()
        {
            return _interpolatedHourlyData._rh;
        }

        public float GetRelativeHumidity(Vector3d simulationPosition)
        {
            return _interpolatedHourlyData._rh;
        }

        public float GetHourlyPrecipitation()
        {
            return _currentHourlyData._precip;
        }

        public float GetHourlyPrecipitation(Vector3d simulationPosition)
        {
            return _currentHourlyData._precip;
        }

        public float WindSpeed { get => _interpolatedHourlyData._windSpeed; }
        public float WindDirection { get => _interpolatedHourlyData._windDirection; }

        public void GetWind(out double speed, out double direction)
        {
            speed = _interpolatedHourlyData._windSpeed;
            direction = _interpolatedHourlyData._windDirection;
        }

        public void GetWind(Vector2d simulationPosition, out double speed, out double direction)
        {
            speed = _interpolatedHourlyData._windSpeed;
            direction = _interpolatedHourlyData._windDirection;
        }

        public void GetWind(Vector3d simulationPosition, out double speed, out double direction)
        {
            speed = _interpolatedHourlyData._windSpeed;
            direction = _interpolatedHourlyData._windDirection;
        }

        public float GetCloudcover(Vector3d simulationPosition)
        {
            return _interpolatedHourlyData._cloudCover;
        }
    }
}
