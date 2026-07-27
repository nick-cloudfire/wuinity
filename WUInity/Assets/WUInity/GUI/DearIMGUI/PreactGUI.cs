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
            ImGui.DockSpace(dockspaceId, Vector2.zero);

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

        LinkedList<string> _messages = new LinkedList<string>();
        public void NewMessage(string message)
        {
            _messages.AddFirst(message);
            if (_messages.Count > 100)
            {
                _messages.RemoveLast();
            }
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
