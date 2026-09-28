//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System;
using System.IO;
using PREACT.Math;

namespace PREACT.Weather
{
    public struct HourlyWeather
    {
        public float _temp, _rh, _precip, _windSpeed, _windDirection, _cloudCover, _directRadiation, _boundrayLayerHeight;

        //temperature_2m", "relative_humidity_2m", "precipitation", "wind_speed_10m", "wind_direction_10m", "cloud_cover", "direct_radiation", "boundary_layer_height" 
        public HourlyWeather(float temp, float rh, float precip, float windSpeed, float windDirection, float cloudCover, float directRadiation, float boundrayLayerHeight)   
        {
            _temp = temp;
            _rh = rh;
            _precip = precip;
            _windSpeed = windSpeed;
            _windDirection = windDirection;
            _cloudCover = cloudCover;
            _directRadiation = directRadiation;
            _boundrayLayerHeight = boundrayLayerHeight;
        }

        public static void InterpolateData(HourlyWeather hour1, HourlyWeather hour2, float fraction, ref HourlyWeather interpolatedHour)
        {
            interpolatedHour._temp = Interpolation.CosineInterpolate(hour1._temp, hour2._temp, fraction);
            interpolatedHour._rh = Interpolation.CosineInterpolate(hour1._rh, hour2._rh, fraction);
            interpolatedHour._precip = Interpolation.CosineInterpolate(hour1._precip, hour2._precip, fraction);
            interpolatedHour._windSpeed = Interpolation.CosineInterpolate(hour1._windSpeed, hour2._windSpeed, fraction);
            //Along the shorter arc: interpolating the raw angles took 350 -> 10 degrees through 180.
            float turn = hour2._windDirection - hour1._windDirection;
            if (turn > 180f) turn -= 360f;
            else if (turn < -180f) turn += 360f;
            float direction = Interpolation.CosineInterpolate(hour1._windDirection, hour1._windDirection + turn, fraction);
            interpolatedHour._windDirection = direction < 0f ? direction + 360f : direction >= 360f ? direction - 360f : direction;
            interpolatedHour._cloudCover = Interpolation.CosineInterpolate(hour1._cloudCover, hour2._cloudCover, fraction);
            interpolatedHour._boundrayLayerHeight = Interpolation.CosineInterpolate(hour1._boundrayLayerHeight, hour2._boundrayLayerHeight, fraction);
        }
    }

    public class WeatherStream
    {
        private double _latitude, _longitude, _elevation;
        private HourlyWeather[] _hourlyData;
        private DateTime _dateTimeFirstEntry;
        private DateTime _dateTimeLastEntry;

        public HourlyWeather[] HourlyData { get => _hourlyData; }
        public double Latitude { get => _latitude; }
        public double Longitude { get => _longitude; }
        public double Elevation { get => _elevation; }
        public DateTime FirstEntry { get => _dateTimeFirstEntry; }
        public DateTime LastEntry { get => _dateTimeLastEntry; }

        public WeatherStream(double latitude, double longitude, double elevation, DateTime firstDateTime, DateTime lastDateTime, HourlyWeather[] hourlyData)
        {
            _latitude = latitude;
            _longitude = longitude;
            _elevation = elevation;
            _hourlyData = hourlyData;
            _dateTimeFirstEntry = firstDateTime;
            _dateTimeLastEntry = lastDateTime;
        }

        /// <summary>
        /// The hour containing <paramref name="dateTime"/> and the one after it (the same hour at the end of the
        /// record). The rows are taken to be consecutive hours from <see cref="FirstEntry"/>; a time outside the
        /// record is clamped to its first or last hour instead of indexing past the array.
        /// </summary>
        public void GetHourlyData(DateTime dateTime, out HourlyWeather current, out HourlyWeather next)
        {
            if (_hourlyData == null || _hourlyData.Length == 0)
            {
                current = default;
                next = default;
                return;
            }

            double hours = (dateTime - _dateTimeFirstEntry).TotalHours;
            int index = hours <= 0 ? 0 : hours >= _hourlyData.Length - 1 ? _hourlyData.Length - 1 : (int)hours;
            current = _hourlyData[index];
            //The last row has no successor; the one before it does (this used to stop one row early).
            next = index + 1 < _hourlyData.Length ? _hourlyData[index + 1] : current;
        }

        private static double ReadHeaderValue(string line)
        {
            string[] parts = (line ?? string.Empty).Split(',');
            return parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value : 0.0;
        }

        public static WeatherStream LoadFromFile(string filePath, out bool success)
        {
            success = false;
            WeatherStream result = null;
            List<HourlyWeather> weatherData = new List<HourlyWeather>();

            bool fileExists = File.Exists(filePath);
            if (fileExists)
            {
                try
                {
                    FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using (StreamReader sr = new StreamReader(fs))
                    {
                        double latitude, longitude, elevation;
                        DateTime first = DateTime.MinValue;
                        DateTime last = DateTime.MaxValue;

                        //Three header rows of name,value: latitude, longitude, elevation. The latitude used to be
                        //parsed from the whole line ("Latitide,38.1"), which never parses, so it was always 0.
                        latitude = ReadHeaderValue(sr.ReadLine());
                        longitude = ReadHeaderValue(sr.ReadLine());
                        elevation = ReadHeaderValue(sr.ReadLine());
                        string line;
                        string[] data;

                        line = sr.ReadLine(); //header line, skip                    
                        line = sr.ReadLine();
                        int j = 4; //we are now at line 4
                        while (line != null)
                        {
                            data = line.Split(',');
                            if (data.Length >= 9)
                            {
                                if (j == 4)
                                {
                                    DateTime.TryParse(data[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out first);
                                }
                                else
                                {
                                    DateTime.TryParse(data[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out last);
                                }

                                float temp, rh, precip, windSpeed, windDirection, cloudCover, directRadiation, boundrayLayerHeight;

                                bool b1 = float.TryParse(data[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out temp);
                                bool b2 = float.TryParse(data[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rh);
                                bool b3 = float.TryParse(data[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out precip);
                                bool b4 = float.TryParse(data[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out windSpeed);
                                bool b5 = float.TryParse(data[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out windDirection);
                                bool b6 = float.TryParse(data[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cloudCover);
                                bool b7 = float.TryParse(data[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out directRadiation);
                                bool b8 = float.TryParse(data[8], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out boundrayLayerHeight);

                                HourlyWeather wD = new HourlyWeather(temp, rh, precip, windSpeed, windDirection, cloudCover, directRadiation, boundrayLayerHeight);
                                weatherData.Add(wD);
                            }

                            j++;
                            line = sr.ReadLine();
                        }

                        if (weatherData.Count > 1 && (last - first).TotalHours + 1.5 < weatherData.Count)
                        {
                            Engine.Message(null, Engine.LogType.Warning, $"Weather input file {filePath} has {weatherData.Count} rows between "
                                + $"{first:yyyy-MM-dd HH:mm} and {last:yyyy-MM-dd HH:mm}, more than one per hour; the rows are read as consecutive hours.");
                        }

                        if (weatherData.Count > 0)
                        {
                            result = new WeatherStream(latitude, longitude, elevation, first, last, weatherData.ToArray());
                            success = true;
                            Engine.Message(null, Engine.LogType.Log, " Weather input file " + filePath + " was found, " + weatherData.Count + " valid data points were succesfully loaded.");
                        }
                        else if (fileExists)
                        {
                            Engine.Message(null, Engine.LogType.Warning, "Weather input file " + filePath + " was found but did not contain any valid data; no weather is reported.");
                        }
                    }                    
                }
                catch (Exception e)
                {
                    Engine.Message(null, Engine.LogType.Exception, e.Message);
                } 
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, "Weather data file " + filePath + " not found; no weather is reported.");
            }

            return result;
        }
    }    
}