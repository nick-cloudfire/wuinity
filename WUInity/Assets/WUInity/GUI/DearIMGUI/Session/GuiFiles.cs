using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The main thread's file probe: every window that wants to know whether a file exists, or what is in
    /// one, asks here, so a question asked by five widgets in one frame costs one stat a second.
    /// </summary>
    public static class GuiFiles
    {
        public static readonly FileProbe Probe = new FileProbe();

        public static bool Exists(string path) => Probe.Exists(path);

        /// <summary>A scenario-relative (or absolute) path made absolute against <paramref name="root"/>, separators normalised.</summary>
        public static string Resolve(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string normalised = path.Replace('\\', '/');
            try
            {
                if (System.IO.Path.IsPathRooted(normalised) || string.IsNullOrEmpty(root))
                {
                    return normalised;
                }
                return System.IO.Path.Combine(root, normalised);
            }
            catch
            {
                return null;
            }
        }
    }
}
