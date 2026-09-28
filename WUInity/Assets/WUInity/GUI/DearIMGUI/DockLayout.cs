using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The default arrangement of the main dockspace: the workflow panel down the left, the console along the
    /// bottom, the map in the middle. Laid out when the dockspace has no arrangement yet, when this layout version
    /// has not run before, and on View &gt; Reset window layout; otherwise ImGui keeps whatever the user arranged.
    /// </summary>
    /// <remarks>
    /// ImGui.NET does not wrap the DockBuilder API (it is in imgui_internal.h), but the cimgui library that
    /// UImGui ships exports it on every platform (igDockBuilder* in win-x64/cimgui.dll, linux-x64/cimgui.so,
    /// osx/cimgui.dylib). It is called directly here. If an entry point is ever missing, the layout is
    /// skipped - windows then open where they were last, or float - and nothing else is affected.
    ///
    /// Where the arrangement is kept: the prefab's UImGui has no IniSettingsAsset, and UImGui then leaves ImGui its
    /// default imgui.ini in the working folder (WUInity/ in the editor), so the dock nodes survive a restart. With
    /// an IniSettingsAsset assigned the asset holds them instead. Whether to lay out is decided by the dock node
    /// itself - does the dockspace have a split? - rather than by a PlayerPrefs flag alone, which disagreed with
    /// imgui.ini as soon as one of the two was deleted and left the panels floating.
    /// </remarks>
    public static class DockLayout
    {
        //Bump when the default arrangement changes, so everyone gets the new one once.
        private const int LayoutVersion = 1;
        private const string VersionKey = "WUInity.DockLayoutVersion";

        private const int ImGuiDockNodeFlags_PassthruCentralNode = 1 << 3;
        private const int ImGuiDockNodeFlags_DockSpace = 1 << 10;
        private const int ImGuiDir_Left = 0;
        private const int ImGuiDir_Down = 3;

        private static bool _resetRequested;
        private static bool _checkedVersion;
        private static bool _unavailable;

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint igDockBuilderAddNode(uint nodeId, int flags);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderRemoveNode(uint nodeId);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint igDockBuilderSplitNode(uint nodeId, int splitDir, float sizeRatioForNodeAtDir,
            out uint outIdAtDir, out uint outIdAtOppositeDir);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderDockWindow(byte[] windowName, uint nodeId);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderFinish(uint nodeId);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr igDockBuilderGetNode(uint nodeId);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool ImGuiDockNode_IsSplitNode(IntPtr node);

        /// <summary>View &gt; Reset window layout: puts the panels back where they start, next frame.</summary>
        public static void RequestReset()
        {
            _resetRequested = true;
            ScenarioWorkflowWindow.Open();
            ConsoleWindow.Open();
        }

        /// <summary>
        /// Called by the dockspace host between ImGui.GetID and ImGui.DockSpace, with the dockspace's ID and
        /// size. Lays the dockspace out when this version has not been applied before, or when asked to.
        /// </summary>
        public static void ApplyIfNeeded(uint dockspaceId, Vector2 size)
        {
            if (_unavailable)
            {
                return;
            }

            if (!_checkedVersion)
            {
                _checkedVersion = true;
                int applied = 0;
                try
                {
                    applied = PlayerPrefs.GetInt(VersionKey, 0);
                }
                catch (Exception)
                {
                    //PlayerPrefs unavailable: decided by the dock node alone.
                    applied = LayoutVersion;
                }

                //Once, at start-up (ImGui has read its settings by the first frame): a dockspace with no split has
                //never been laid out, or its arrangement was lost; a newer layout version is applied once.
                bool arranged;
                try
                {
                    IntPtr node = igDockBuilderGetNode(dockspaceId);
                    arranged = node != IntPtr.Zero && ImGuiDockNode_IsSplitNode(node);
                }
                catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException)
                {
                    arranged = false;
                }

                if (!arranged || applied < LayoutVersion)
                {
                    _resetRequested = true;
                }
            }

            if (!_resetRequested || size.x <= 0f || size.y <= 0f)
            {
                return;
            }
            _resetRequested = false;

            try
            {
                igDockBuilderRemoveNode(dockspaceId);
                igDockBuilderAddNode(dockspaceId, ImGuiDockNodeFlags_DockSpace | ImGuiDockNodeFlags_PassthruCentralNode);
                igDockBuilderSetNodeSize(dockspaceId, size);

                //Left: the workflow, about a quarter of the width but never narrower than its rows need.
                float leftRatio = Mathf.Clamp(380f / size.x, 0.2f, 0.4f);
                igDockBuilderSplitNode(dockspaceId, ImGuiDir_Left, leftRatio, out uint left, out uint rest);

                //Bottom of the rest: the console. The remaining central node is the map, and passes the mouse through.
                float bottomRatio = Mathf.Clamp(220f / size.y, 0.15f, 0.35f);
                igDockBuilderSplitNode(rest, ImGuiDir_Down, bottomRatio, out uint bottom, out uint _);

                igDockBuilderDockWindow(Name(ScenarioWorkflowWindow.WindowId), left);
                igDockBuilderDockWindow(Name("###Console"), bottom);
                igDockBuilderFinish(dockspaceId);

                PlayerPrefs.SetInt(VersionKey, LayoutVersion);
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                //Any failure, not only a missing entry point: this runs inside the dockspace host window, and an
                //exception out of it would skip that window's End and its style pops every frame.
                _unavailable = true;
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning,
                    "The default window layout could not be applied (" + e.Message + "); windows float instead.");
            }
        }

        //ImGui hashes a window's name from its "###" marker when there is one, so "###Workflow" names the same
        //window as "Scenario workflow###Workflow".
        private static byte[] Name(string windowName)
        {
            byte[] text = Encoding.UTF8.GetBytes(windowName);
            byte[] zeroTerminated = new byte[text.Length + 1];
            Buffer.BlockCopy(text, 0, zeroTerminated, 0, text.Length);
            return zeroTerminated;
        }
    }
}
