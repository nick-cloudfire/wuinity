using GeoTimeZone;
using System;

namespace PREACT
{    
    public  class TimeManager
    {
        DateTime _startDateTime;
        DateTime _endDateTime;
        DateTime _currentDateTime;
        DateTime _currentUTCDateTime;
        //Double, and derived from the step count rather than accumulated: a float clock drifted 142 s over a
        //day at dt = 0.2 (746 s at dt = 0.1) while CurrentDateTime stayed exact, so the weather and the modules
        //disagreed about what time it was.
        double _simulationTime;
        double _simulationEndTime;
        long _steps;
        double _stepLength;
        //string _startDateISO8601;
        //string _endDateISO8601;

        public double SimulationTime { get => _simulationTime; }
        public double SimulationEndTime { get => _simulationEndTime; }
        public DateTime StartDateTime { get => _startDateTime; }
        public DateTime EndDateTime { get => _endDateTime; }
        public DateTime CurrentDateTime { get => _currentDateTime; }
        public DateTime CurrentUTCDateTime { get => _currentUTCDateTime; }
        //public string StartDateISO8601 { get => _startDateISO8601; }
        //public string EndDateISO8601 { get => _endDateISO8601; }

        public TimeManager(Input.PREACTInput input, Simulation simulation)
        {
            _simulationTime = 0;

            TimeZoneResult iana = TimeZoneLookup.GetTimeZone(simulation.Input.Simulation.LowerLeftLatLon.x, simulation.Input.Simulation.LowerLeftLatLon.y);
            string windows = TimeZoneConverter.TZConvert.IanaToWindows(iana.Result);
            TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById(windows);
            DateTimeOffset dto = new DateTimeOffset(input.Simulation.StartDateTime, tz.GetUtcOffset(input.Simulation.StartDateTime));

            _startDateTime = dto.DateTime;
            _currentDateTime = _startDateTime;
            _currentUTCDateTime = dto.UtcDateTime;
            _startUTCDateTime = _currentUTCDateTime;
            //Interpret the end time in the simulation location's timezone too, so the
            //run duration is (End - Start) as entered and does not depend on the host
            //machine's timezone (ToLocalTime() shifted it by the host UTC offset).
            DateTimeOffset endDto = new DateTimeOffset(input.Simulation.EndDateTime, tz.GetUtcOffset(input.Simulation.EndDateTime));
            _endDateTime = endDto.DateTime;
            _simulationEndTime = (_endDateTime - _startDateTime).TotalSeconds;

            Engine.Message(simulation, Engine.LogType.Log, $"Simulation will run between {_startDateTime:yyyy-MM-dd HH:mm:ss} and {_endDateTime:yyyy-MM-dd HH:mm:ss} for a total of {_simulationEndTime:F0} seconds (unless user has specified to exit early once evacuated.)");

            //_startDateISO8601 = new string($"{_startDateTime.Year}-{_startDateTime.Month}-{_startDateTime.Day}");
            //_endDateISO8601 = new string($"{_endDateTime.Year}-{_endDateTime.Month}-{_endDateTime.Day}");
        }

        public void Step(double deltaTime)
        {
            //A constant step is the normal case, and then the time is the step count times the step exactly.
            if (_steps == 0 || deltaTime != _stepLength)
            {
                _stepLength = deltaTime;
                _stepOrigin = _simulationTime;
                _steps = 0;
            }
            ++_steps;
            _simulationTime = _stepOrigin + _steps * _stepLength;
            _currentDateTime = _startDateTime.AddTicks((long)System.Math.Round(_simulationTime * TimeSpan.TicksPerSecond));
            _currentUTCDateTime = _startUTCDateTime.AddTicks((long)System.Math.Round(_simulationTime * TimeSpan.TicksPerSecond));
        }

        private double _stepOrigin;
        private DateTime _startUTCDateTime;

        public double GetSimulationTime(DateTime dateTime)
        {
            TimeSpan delta = dateTime - _startDateTime;
            return delta.TotalSeconds;
        }
    }
}
