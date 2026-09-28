namespace WUInity.Workflow
{
    /// <summary>
    /// The thirteen steps from an empty folder to a trigger campaign, in the order they are done. The number
    /// is what the workflow panel and the menus show, so a step keeps it whatever variant is in force.
    /// </summary>
    public enum WorkflowStepId
    {
        None = 0,
        PlaceAndTime = 1,
        Roads = 2,
        Population = 3,
        Fuels = 4,
        FireCase = 5,
        FireAreas = 6,
        Destinations = 7,
        CurvesAndDemographics = 8,
        EvacuationGroups = 9,
        TriggerBoundary = 10,
        RunSimulation = 11,
        Results = 12,
        Campaign = 13,
    }
}
