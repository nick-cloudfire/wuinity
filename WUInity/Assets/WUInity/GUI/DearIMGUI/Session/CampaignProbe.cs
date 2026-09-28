using System.Reflection;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>Whether the probabilistic trigger campaign window has a campaign process running.</summary>
    internal static class CampaignProbe
    {
        // V1-INTEGRATION: ProbabilisticTriggerWindow belongs to WP1 and has no public "is a campaign running"
        // member, so this reads its private _running flag. Once it exposes one (a `public static bool
        // IsRunning` property is looked for first), the reflection on the field can go.
        private static readonly PropertyInfo _isRunningProperty =
            typeof(ProbabilisticTriggerWindow).GetProperty("IsRunning", BindingFlags.Public | BindingFlags.Static);

        private static readonly FieldInfo _runningField =
            typeof(ProbabilisticTriggerWindow).GetField("_running", BindingFlags.NonPublic | BindingFlags.Static);

        public static bool IsRunning
        {
            get
            {
                try
                {
                    if (_isRunningProperty != null && _isRunningProperty.PropertyType == typeof(bool))
                    {
                        return (bool)_isRunningProperty.GetValue(null);
                    }

                    if (_runningField != null && _runningField.FieldType == typeof(bool))
                    {
                        return (bool)_runningField.GetValue(null);
                    }
                }
                catch
                {
                    //A renamed or retyped member reads as "not running" rather than breaking every frame.
                }

                return false;
            }
        }
    }
}
