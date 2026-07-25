using System.Collections.Generic;

namespace PREACT.Detection
{
    /// <summary>
    /// Scaffold for hazard-detection modules (e.g. drone or satellite based
    /// detection). No detection module is implemented at the moment, so this is
    /// a no-op that the simulation can still drive uniformly with the other
    /// managers.
    /// </summary>
    public class DetectionManager
    {
        private Simulation _simulation;

        public DetectionManager(Simulation simulation)
        {
            _simulation = simulation;
        }

        public List<SimulationModule> CreateModules(WeatherManager weather, TimeManager time, out bool success)
        {
            success = true;
            return new List<SimulationModule>();
        }

        public void PostStep(TimeManager time, float deltaTime)
        {
        }
    }
}
