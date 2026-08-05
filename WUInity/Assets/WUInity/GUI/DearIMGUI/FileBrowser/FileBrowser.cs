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
        public static readonly string[] fuelModelsFilter = new string[] { ".fuel" };

        public static void Cancel()
        {

        }

        public static void OpenLoadInput()
        {
            SimpleFileBrowser.FileBrowser.SetFilters(false, wuiFilter);
            string initialPath = PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(LoadInput, Cancel, SimpleFileBrowser.FileBrowser.PickMode.Files, false, initialPath, null, "Load WUI file", "Load");
        }
        private static void LoadInput(string[] paths)
        {
            bool success;
            PreactGUI.Engine.LoadInputFromFile(paths[0], out success);
            //Shown whether or not the load was complete: an incomplete scenario is now opened rather
            //than refused, and the checklist is how that gets said.
            ScenarioChecklistWindow.ShowFor(Path.GetFileName(paths[0]));
        }

        public static void OpenSaveInput()
        {
            SimpleFileBrowser.FileBrowser.SetFilters(false, wuiFilter);
            string initialPath = PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowSaveDialog(ScenarioEditorWindow.SaveNewInput, Cancel, SimpleFileBrowser.FileBrowser.PickMode.Files, false, initialPath, ".wui", "Save file", "Save");
        }

        private static bool _getRelativePath;
        private static Action<string> _onFileSet;
        public static void OpenSetFilePath(Action<string> onFileSet, string dialogHeader, bool getRelativePath, string[] fileFilter = null)
        {
            _onFileSet = onFileSet;
            _getRelativePath = getRelativePath;

            if(fileFilter == null)
            {
                SimpleFileBrowser.FileBrowser.SetFilters(true);
            }
            else
            {
                SimpleFileBrowser.FileBrowser.SetFilters(true, fileFilter);
            }
            string initialPath = PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(SetFilePath, Cancel, SimpleFileBrowser.FileBrowser.PickMode.Files, false, initialPath, null, dialogHeader, "Set");
        }

        private static void SetFilePath(string[] paths)
        {
            string filePath = paths[0];
            if (_getRelativePath)
            {
                filePath = Path.GetRelativePath(PreactGUI.Engine.WorkingFolder, paths[0]);
            }
            _onFileSet?.Invoke(filePath);
            //_onFileSet = null;
        }

        /// <summary>
        /// Picks a directory. Returned relative to the scenario folder when it is inside it, absolute
        /// otherwise.
        /// </summary>
        /// <remarks>
        /// Conditional rather than always relative, unlike <see cref="OpenSetFilePath"/>: the folders asked for
        /// here are as often outside the scenario as in it — a GDAL bin directory, an ELMFIRE install — and
        /// <c>Path.GetRelativePath</c> answers those with a chain of <c>..</c> that is correct, unreadable, and
        /// breaks as soon as the scenario is moved.
        /// </remarks>
        public static void OpenSetFolderPath(Action<string> onFolderSet, string dialogHeader)
        {
            _onFileSet = onFolderSet;
            SimpleFileBrowser.FileBrowser.SetFilters(true);
            string initialPath = PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(SetFolderPath, Cancel,
                SimpleFileBrowser.FileBrowser.PickMode.Folders, false, initialPath, null, dialogHeader, "Set");
        }

        private static void SetFolderPath(string[] paths)
        {
            string folder = paths[0];
            string root = PreactGUI.Engine.WorkingFolder;

            if (!string.IsNullOrEmpty(root))
            {
                string relative = Path.GetRelativePath(root, folder);
                if (!relative.StartsWith("..") && !Path.IsPathRooted(relative))
                {
                    folder = relative;
                }
            }

            _onFileSet?.Invoke(folder);
        }

        /*public static void OpenCreateBaseData()
        {
            FileBrowser.ShowSaveDialog(NewScenarioWindow.CreateBaseData, CancelSaveLoad, FileBrowser.PickMode.Folders, false, null, null, "Select root folder", "Create data");
        }*/

    }
}
