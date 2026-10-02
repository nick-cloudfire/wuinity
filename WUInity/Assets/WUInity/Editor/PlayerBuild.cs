using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace WUInity.Build
{
    /// <summary>
    /// Builds the standalone Windows player, <c>WUInity.exe</c> with its <c>WUInity_Data</c> folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Editor-only (this folder is named Editor, so Unity compiles it into Assembly-CSharp-Editor and never into a
    /// player). <c>build-player.ps1</c> at the repository root runs it in batch mode and then copies PREACT.exe,
    /// PREACTcli and ELMFIRE beside the player; see <c>docs/distribution.md</c>:
    /// </para>
    /// <code>
    /// Unity.exe -batchmode -quit -buildTarget Win64 -projectPath WUInity -logFile build.log
    ///           -executeMethod WUInity.Build.PlayerBuild.BuildFromCommandLine -buildOutput dist\WUInity\WUInity.exe
    ///           [-developmentBuild]
    /// </code>
    /// <para>
    /// The menu item WUInity &gt; Build standalone player does the same from an open editor, without the copies.
    /// </para>
    /// <para>
    /// What is built is the scenes enabled in the Build Settings (Assets/WUInity/Scenes/WUInityMain.unity), with the
    /// engine DLLs build.ps1 put into Assets/PREACT/Release, everything under Assets/StreamingAssets copied as files
    /// (the GUI font), and every Resources folder built in - including Assets/Resources/Mapbox/MapboxConfiguration.txt
    /// and Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt when they exist, which is what this says
    /// before it builds: a player made with them carries those keys to whoever gets a copy.
    /// </para>
    /// </remarks>
    public static class PlayerBuild
    {
        /// <summary>Where the player goes by default, relative to the Unity project folder (WUInity/).</summary>
        public const string DefaultOutput = "../dist/WUInity/WUInity.exe";

        private const string EngineDll = "Assets/PREACT/Release/netstandard2.1/PREACTcore.dll";
        private const string Font = "Assets/StreamingAssets/Fonts/AdobeClean-Regular.otf";

        private static readonly string[] KeyFiles =
        {
            "Assets/Resources/Mapbox/MapboxConfiguration.txt",
            "Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt",
        };

        /// <summary>The Unity project folder (the one holding Assets/).</summary>
        private static string ProjectFolder => Path.GetDirectoryName(Application.dataPath);

        [MenuItem("WUInity/Build standalone player...")]
        private static void BuildFromMenu()
        {
            string exe = Path.GetFullPath(Path.Combine(ProjectFolder, DefaultOutput));
            string keys = KeysBuiltIn();
            if (!EditorUtility.DisplayDialog("Build standalone player",
                    "Builds the Windows player into\n" + exe + "\n\n"
                    + "This builds the player only. build-player.ps1 (repository root) also copies PREACT.exe, PREACTcli "
                    + "and ELMFIRE beside it, which campaigns and fires need."
                    + (keys.Length > 0 ? "\n\nBuilt in, for whoever gets a copy: " + keys + "." : ""),
                    "Build", "Cancel"))
            {
                return;
            }

            Build(exe, false, out string problem);
            if (problem != null)
            {
                EditorUtility.DisplayDialog("Build standalone player", "The build failed: " + problem, "OK");
            }
            else
            {
                EditorUtility.RevealInFinder(exe);
            }
        }

        /// <summary>
        /// The batch-mode entry point: reads <c>-buildOutput &lt;exe&gt;</c> (default <see cref="DefaultOutput"/>) and
        /// <c>-developmentBuild</c>, builds, and exits Unity with 0 when the player was built and 1 otherwise.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            int exitCode = 1;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string output = Argument(args, "-buildOutput");
                string exe = Path.GetFullPath(string.IsNullOrEmpty(output) ? Path.Combine(ProjectFolder, DefaultOutput) : output);
                Build(exe, args.Contains("-developmentBuild"), out string problem);
                if (problem != null)
                {
                    Debug.LogError("PlayerBuild: " + problem);
                }
                else
                {
                    exitCode = 0;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("PlayerBuild: the build threw " + e);
            }

            EditorApplication.Exit(exitCode);
        }

        /// <summary>
        /// Builds the player to <paramref name="exePath"/>. <paramref name="problem"/> is null when it was built, else why
        /// not; the report is null when the build did not start.
        /// </summary>
        public static BuildReport Build(string exePath, bool development, out string problem)
        {
            problem = Check(exePath);
            if (problem != null)
            {
                return null;
            }

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            string keys = KeysBuiltIn();
            if (keys.Length > 0)
            {
                Debug.LogWarning("PlayerBuild: built into the player, for whoever gets a copy of it: " + keys + ".");
            }
            if (!File.Exists(Path.Combine(ProjectFolder, Font)))
            {
                Debug.LogWarning("PlayerBuild: " + Font + " is not there; the player will use Dear ImGui's own font.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(exePath));
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = exePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };

            Debug.Log("PlayerBuild: building " + string.Join(", ", scenes) + " into " + exePath
                      + (development ? " (development build)" : "") + ".");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
            {
                problem = $"the player build ended {summary.result} with {summary.totalErrors} error(s); the messages above say why.";
                return report;
            }
            if (!File.Exists(exePath))
            {
                problem = "the build reported success but " + exePath + " is not there.";
                return report;
            }

            Debug.Log($"PlayerBuild: built {exePath}, {summary.totalSize / (1024.0 * 1024.0):F0} MB, in "
                      + $"{(summary.buildEndedAt - summary.buildStartedAt).TotalMinutes:F1} min.");
            return report;
        }

        /// <summary>Why the build cannot start, or null.</summary>
        private static string Check(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return "the output must be an .exe path (it is " + (exePath ?? "empty") + ").";
            }
            if (!File.Exists(Path.Combine(ProjectFolder, EngineDll)))
            {
                return EngineDll + " is not there: run build.ps1 first, which builds the engine into the Unity project.";
            }
            if (!EditorBuildSettings.scenes.Any(s => s.enabled))
            {
                return "no scene is enabled in File > Build Settings; WUInityMain.unity should be.";
            }

            //A player built into the project's own Assets would be imported by Unity as assets on the next refresh.
            string assets = Path.GetFullPath(Application.dataPath).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(exePath).StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            {
                return "the output is inside Assets/, which Unity would import; build into dist/ beside the project.";
            }
            return null;
        }

        /// <summary>The key files a build would carry, comma-separated; empty when there are none.</summary>
        private static string KeysBuiltIn()
        {
            var present = new List<string>();
            foreach (string file in KeyFiles)
            {
                if (File.Exists(Path.Combine(ProjectFolder, file)))
                {
                    present.Add(file);
                }
            }
            return string.Join(", ", present);
        }

        private static string Argument(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; ++i)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }
    }
}
