namespace PREACT
{
    /// <summary>
    /// What the engine tells whoever drives it (the Unity GUI, PREACT.exe, the tests): the input it took on, its
    /// log, the end of a run, and destinations added during one.
    /// </summary>
    /// <remarks>
    /// SimulationStarted, StopSimulations and PauseSimulations were declared here too, but the engine never
    /// called them: stopping and pausing go the other way, through <see cref="Engine.CloseSimulations"/> and
    /// <see cref="Simulation.TogglePause"/>.
    /// </remarks>
    public interface IExternalManager
    {
        void UpdateInput(Input.PREACTInput input);
        void NewLogMessage(string message);
        void SimulationsFinished();
        void UpdateDestinations(System.Collections.Generic.List<Evacuation.EvacuationDestination> destinations);
    }
}