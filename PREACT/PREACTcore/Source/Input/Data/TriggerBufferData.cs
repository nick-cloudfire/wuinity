using PREACT.Input;
using PREACT.Wildfire;
using System.IO;

namespace PREACT.Input
{
    public class TriggerBufferData
    {

        private InitialFuelMoistureLibrary _kPERILInitialFuelMoistureData;
        private FuelModelInput _kPERILFuelModelsData;

        public InitialFuelMoistureLibrary kPERILInitialFuelMoistureData { get => _kPERILInitialFuelMoistureData; }

        /// <summary>
        /// The BEHAVE fuel model table k-PERIL derives its rate of spread with.
        ///
        /// Its own, rather than borrowed from whichever fire module happens to be selected. It used to come
        /// from the cell-based spread model's settings, which meant deriving a trigger boundary with BEHAVE
        /// depended on a fire module that had nothing to do with it - and once the fire comes from ELMFIRE
        /// there is no such section to borrow from at all.
        /// </summary>
        public FuelModelInput kPERILFuelModelsData { get => _kPERILFuelModelsData; }


        public TriggerBufferData()
        {

        }

        public void LoadAll(SimulationInput simulationInput, TriggerBufferModuleInput triggerBufferInput, string rootFolder, out bool success)
        {
            success = true;

            if (!triggerBufferInput.Enabled
                || triggerBufferInput.Module != TriggerBufferModuleInput.TriggerBufferModules.kPERIL
                || !triggerBufferInput.kPERILInput.CalculateROSFromBehave)
            {
                //Nothing to load: with the rate of spread supplied by the fire module, k-PERIL reads neither
                //of these.
                return;
            }

            if (!string.IsNullOrEmpty(triggerBufferInput.kPERILInput.InitialFuelMoistureFile))
            {
                _kPERILInitialFuelMoistureData = InitialFuelMoistureLibrary.LoadInitialFuelMoistureDataFile(
                    Path.Combine(rootFolder, triggerBufferInput.kPERILInput.InitialFuelMoistureFile), out success);
                if (!success)
                {
                    return;
                }
            }

            if (!string.IsNullOrEmpty(triggerBufferInput.kPERILInput.FuelModelsFile))
            {
                _kPERILFuelModelsData = FuelModelInput.LoadFromFile(
                    Path.Combine(rootFolder, triggerBufferInput.kPERILInput.FuelModelsFile), out success);
                if (!success)
                {
                    return;
                }
            }

            success = true;
        }
    }
}