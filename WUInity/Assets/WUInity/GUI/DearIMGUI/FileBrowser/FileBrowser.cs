using SimpleFileBrowser;
using System;
using System.IO;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class FileBrowser
    {
        //filters
        public static readonly string[] wuiFilter = new string[] { ".wui" };
        public static readonly string[] lcpFilter = new string[] { ".lcp", ".tif", ".tiff" };
        public static readonly string[] geoTiffFilter = new string[] { ".tif", ".tiff" };

        public static void Cancel()
        {

        }

        public static void OpenLoadInput()
        {
            SimpleFileBrowser.FileBrowser.SetFilters(false, wuiFilter);
            string initialPath = ScenarioSession.HasInput
                ? Path.GetDirectoryName(ScenarioSession.RootFolder.TrimEnd('\\', '/'))
                : PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(LoadInput, Cancel, SimpleFileBrowser.FileBrowser.PickMode.Files, false, initialPath, null, "Load WUI file", "Load");
        }
        private static void LoadInput(string[] paths)
        {
            ScenarioSession.Load(paths[0]);
        }

        /// <summary>Picks the folder File &gt; Copy scenario to... copies the scenario's folder into.</summary>
        public static void OpenCopyScenario(bool includeOutputs)
        {
            SimpleFileBrowser.FileBrowser.SetFilters(true);
            string initialPath = ScenarioSession.HasInput
                ? Path.GetDirectoryName(ScenarioSession.RootFolder.TrimEnd('\\', '/'))
                : PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(paths => ScenarioSession.CopyTo(paths[0], includeOutputs), Cancel,
                SimpleFileBrowser.FileBrowser.PickMode.Folders, false, initialPath, null, "Copy the scenario into", "Copy here");
        }

        public static void OpenSaveInput()
        {
            SimpleFileBrowser.FileBrowser.SetFilters(false, wuiFilter);
            //Opened in the scenario's own folder, which is the only folder Save as writes into.
            string initialPath = ScenarioSession.RootFolder ?? PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowSaveDialog(paths => ScenarioSession.SaveAs(paths[0]), Cancel,
                SimpleFileBrowser.FileBrowser.PickMode.Files, false, initialPath, ScenarioSession.DisplayName + ".wui",
                "Save scenario as (in its own folder)", "Save");
        }

        private static string _relativeTo;
        private static Action<string> _onFileSet;

        /// <summary>
        /// Picks a file. The result is relative to <paramref name="relativeTo"/> when it is inside that folder,
        /// absolute otherwise, and always uses forward slashes.
        /// </summary>
        /// <remarks>
        /// The folder is passed in rather than taken from the engine. It used to be
        /// <c>Engine.WorkingFolder</c> always - which while a new scenario is being created is the previous
        /// scenario's folder, or the executable's - so every file picked in the creator was stored relative to
        /// the wrong place. And <c>Path.GetRelativePath</c> answered a file outside the scenario with a chain
        /// of <c>..</c> that is correct, unreadable and broken the moment the scenario is moved, and with
        /// backslashes on Windows, which are not a separator anywhere else.
        /// </remarks>
        /// <param name="relativeTo">The scenario folder the path is written relative to; null keeps it absolute.</param>
        public static void OpenSetFilePath(Action<string> onFileSet, string dialogHeader, string relativeTo, string[] fileFilter = null)
        {
            _onFileSet = onFileSet;
            _relativeTo = relativeTo;

            if(fileFilter == null)
            {
                SimpleFileBrowser.FileBrowser.SetFilters(true);
            }
            else
            {
                SimpleFileBrowser.FileBrowser.SetFilters(true, fileFilter);
            }
            string initialPath = !string.IsNullOrEmpty(relativeTo) && Directory.Exists(relativeTo)
                ? relativeTo
                : PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(SetPickedPath, Cancel, SimpleFileBrowser.FileBrowser.PickMode.Files, false, initialPath, null, dialogHeader, "Set");
        }

        private static void SetPickedPath(string[] paths)
        {
            _onFileSet?.Invoke(RelativeIfInside(_relativeTo, paths[0]));
        }

        /// <summary>
        /// Picks a directory. Relative to <paramref name="relativeTo"/> when it is inside it, absolute
        /// otherwise; forward slashes either way.
        /// </summary>
        public static void OpenSetFolderPath(Action<string> onFolderSet, string dialogHeader, string relativeTo)
        {
            _onFileSet = onFolderSet;
            _relativeTo = relativeTo;
            SimpleFileBrowser.FileBrowser.SetFilters(true);
            string initialPath = !string.IsNullOrEmpty(relativeTo) && Directory.Exists(relativeTo)
                ? relativeTo
                : PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(SetPickedPath, Cancel,
                SimpleFileBrowser.FileBrowser.PickMode.Folders, false, initialPath, null, dialogHeader, "Set");
        }

        /// <summary>
        /// <paramref name="path"/> relative to <paramref name="root"/> when it lies inside it, else absolute;
        /// forward slashes. The one rule every path written into a scenario follows.
        /// </summary>
        public static string RelativeIfInside(string root, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string result = path;
            try
            {
                string full = Path.GetFullPath(path);
                result = full;

                if (!string.IsNullOrEmpty(root))
                {
                    string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
                    //Case-insensitively on Windows, where C:\Cases and c:\cases are the same folder.
                    StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;

                    if (full.Length > rootFull.Length
                        && full.StartsWith(rootFull, comparison)
                        && (full[rootFull.Length] == '\\' || full[rootFull.Length] == '/'))
                    {
                        result = full.Substring(rootFull.Length + 1);
                    }
                }
            }
            catch
            {
                //Not a path the file system can make sense of: returned as given, normalised.
            }

            return result.Replace('\\', '/');
        }

    }
}
