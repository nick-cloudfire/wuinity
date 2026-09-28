using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// What the Unity GUI relies on the engine for and cannot test itself (it has no test runner): how a painting records
    /// its grid and who checks that record, and which files in a results folder are results.
    /// </summary>
    internal static class GuiContractTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("painting: a read-only .gfi loads", ReadOnlyPainting);
        }

        private static void ReadOnlyPainting()
        {
            string folder = Directory.CreateTempSubdirectory("preact-gfi-").FullName;
            try
            {
                var data = new Input.WildfireData { WuiArea = new bool[12], InitialIgnition = new bool[12] };
                data.WuiArea[3] = true;
                data.InitialIgnition[7] = true;
                string gfi = Path.Combine(folder, "painted.gfi");
                GraphicalFireInput.SaveGraphicalFireInput(gfi, data, 4, 3);
                if (OperatingSystem.IsWindows()) File.SetAttributes(gfi, FileAttributes.ReadOnly);
                else File.SetUnixFileMode(gfi, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

                //Held open for reading elsewhere as well, as the GUI's model does while it counts the cells.
                using (new FileStream(gfi, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    GraphicalFireInput.LoadGraphicalFireInput(gfi, out int ncols, out int nrows, out bool[] wui, out bool[] _,
                        out bool[] initial, out bool[] _, out bool ok);
                    Assert.True(ok && ncols == 4 && nrows == 3 && wui[3] && initial[7], "a read-only painting loads");
                }
            }
            finally
            {
                foreach (string f in Directory.GetFiles(folder)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch { } }
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
