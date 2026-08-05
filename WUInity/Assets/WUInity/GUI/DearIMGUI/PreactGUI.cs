using Assets.WUInity.GUI.DearIMGUI.Editors;
using ImGuiNET;
using PREACT;
using System;
using System.Collections.Generic;
using System.IO;
using UImGui;
using UnityEngine;
using WUInity;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public class PreactGUI : MonoBehaviour
    {
        [SerializeField]
        private bool darkTheme = false; 
        string fontFilePath = $"{Application.dataPath}/WUInity/GUI/DearIMGUI/Fonts/AdobeClean-Regular.otf"; //MS_Sans_Serif.ttf

        static WUInityManager _wuinityManager;
        static Engine _engine;

        public static WUInityManager WUInity { get => _wuinityManager; }
        public static Engine Engine { get => _engine; }

        private void OnEnable()
        {
            UImGuiUtility.Layout += OnLayout;

            //The theme can only be applied once UImGui has created the ImGui context, and the
            //order in which the two components are enabled is not guaranteed. Applying it here
            //alone therefore works or throws depending on that order. Subscribing to
            //OnInitialize covers the case where UImGui comes second; the direct call covers the
            //case where it has already initialised (the event would then never fire again).
            UImGuiUtility.OnInitialize += OnImGuiInitialized;
            ApplyTheme();
        }

        private void OnDisable()
        {
            UImGuiUtility.Layout -= OnLayout;
            UImGuiUtility.OnInitialize -= OnImGuiInitialized;
        }

        private void OnImGuiInitialized(UImGui.UImGui obj)
        {
            ApplyTheme();
        }

        private static event Action Windows;
        public static void DrawWindow(Action window)
        {
            Windows += window;
        }

        public static void CloseWindow(Action window)
        {
            Windows -= window;
        }

        //draws menus
        private void OnLayout(UImGui.UImGui obj)
        {            
            if(_wuinityManager == null)
            {
                return;
            }

            MainDock();
            MainMenuBar.Draw();      
            ConsoleWindow.Draw(_messages);

            //windows
            if(Windows != null)
            {
                Windows.Invoke();
            }           
        }

        public static ImGuiWindowFlags NoDockingNoCollapse = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse;

        private static ImGuiWindowFlags mainDockspace =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground |        
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        static void MainDock()
        {
            ImGuiViewportPtr viewport = ImGui.GetMainViewport();

            ImGui.SetNextWindowPos(viewport.WorkPos);
            ImGui.SetNextWindowSize(viewport.WorkSize);
            ImGui.SetNextWindowViewport(viewport.ID);

            // Remove all visible borders
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);

            // Remove background colors
            ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
            ImGui.PushStyleColor(ImGuiCol.DockingEmptyBg, 0);
            ImGui.PushStyleColor(ImGuiCol.Border, 0);
            ImGui.PushStyleColor(ImGuiCol.BorderShadow, 0);

            ImGui.Begin("MainDockspaceHost", mainDockspace);

            uint dockspaceId = ImGui.GetID("MainDockspace");
            //PassthruCentralNode is what makes the map usable. This host window covers the whole
            //viewport, and without the flag the dockspace's empty central node is a solid, hoverable
            //node across all of it - so ImGui reported WantCaptureMouse everywhere, at all times, and
            //everything that defers to the GUI for the mouse (panning, zooming, picking a position,
            //the brush) was permanently switched off. NoBackground on the host, already set above, is
            //the other half of the pair this flag expects.
            ImGui.DockSpace(dockspaceId, Vector2.zero, ImGuiDockNodeFlags.PassthruCentralNode);

            ImGui.End();

            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar(3);

        }

        public void SetManager(WUInityManager wuinityManager, Engine engine, PREACT.Runtime.WorkingData workingData)
        {
            _wuinityManager = wuinityManager;
            _engine = engine;
            StartWindow.Open();
            //_workingData = workingData;
        }


        public void SetInput(PREACT.Input.PREACTInput input)
        {
            ScenarioEditorWindow.SetInput(input);
        }

        //Static like _engine and _wuinityManager above, so the menu bar - which is static - can
        //clear it. Without this the Console/Clear item had nothing to act on and did nothing.
        //
        //Oldest first, and the whole session: the console draws only the tail of it, but copying and
        //saving want everything. This used to be a 100-entry ring buffer, which meant the earliest
        //messages - where the first error in a failed run usually is - were gone before they could be
        //read. The engine keeps its own log the same way, unbounded, for the same reason.
        static List<string> _messages = new List<string>();
        public static IReadOnlyList<string> Messages { get => _messages; }

        public void NewMessage(string message)
        {
            _messages.Add(message);
        }

        public static void ClearMessages()
        {
            _messages.Clear();
        }

        bool _simulationRunning = false;
        public void SimulationStarted()
        {
            _messages.Clear();
            _simulationRunning = true;
        }

        public void SimulationsFinished()
        {
            _simulationRunning = false;
        }

        public void ApplyTheme()
        {
            Themes.ApplyAdobeSpectrum(darkTheme);
        }

        public void AddFont(ImGuiIOPtr io)
        {
            // Clear default fonts if you want only your custom one            
            io.Fonts.Clear();
            io.Fonts.AddFontFromFileTTF(fontFilePath, 14);
        }        
    }
}
