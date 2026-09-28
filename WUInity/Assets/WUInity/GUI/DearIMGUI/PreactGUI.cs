using Assets.WUInity.GUI.DearIMGUI.Editors;
using ImGuiNET;
using PREACT;
using System;
using System.Collections.Concurrent;
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
            DrainMainThreadQueues();

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
            //Once at start-up, off the main thread; the answers are read from ToolsService.Current.
            ToolsService.Refresh();
        }


        //Static like _engine and _wuinityManager above, so the menu bar - which is static - can
        //clear it.
        //
        //Oldest first, and the whole session: the console draws only the tail of it, but copying and
        //saving want everything. The engine keeps its own log the same way, unbounded, for the same reason.
        //
        //Main thread only. Messages arrive from every thread there is - the simulation (the whole run is a
        //Task.Run), the data steps' workers, the downloaders' own tasks - and this list used to be appended
        //to directly from all of them while the console iterated it by index on the main thread, which is a
        //torn read at best and an exception from inside a resize at worst. They are queued now and moved
        //across at the start of every frame by DrainMainThreadQueues.
        static readonly List<string> _messages = new List<string>();
        static readonly ConcurrentQueue<string> _incomingMessages = new ConcurrentQueue<string>();
        public static IReadOnlyList<string> Messages { get => _messages; }

        /// <summary>Queues a console line. Safe from any thread.</summary>
        public void NewMessage(string message)
        {
            _incomingMessages.Enqueue(message);
        }

        public static void ClearMessages()
        {
            _messages.Clear();
        }

        //Work handed to the main thread: path writes from data steps, campaign status, anything that
        //touches the scenario, ImGui or a UnityEngine object. Run in order, once each, at the start of the
        //next frame.
        static readonly ConcurrentQueue<Action> _posted = new ConcurrentQueue<Action>();

        /// <summary>
        /// Runs <paramref name="action"/> on the main thread at the start of the next frame. Safe from any
        /// thread; the one way worker code should reach the scenario, the GUI or Unity.
        /// </summary>
        public static void Post(Action action)
        {
            if (action != null)
            {
                _posted.Enqueue(action);
            }
        }

        /// <summary>
        /// Moves queued console lines into the list and runs posted work. Called from Update and again at
        /// the start of the ImGui layout, so it happens every frame whether or not the GUI is drawing.
        /// </summary>
        private static void DrainMainThreadQueues()
        {
            while (_incomingMessages.TryDequeue(out string message))
            {
                _messages.Add(message);
            }

            //Bounded per frame, so something that posts from inside a posted action cannot keep this
            //loop alive forever; the remainder runs next frame.
            int budget = 256;
            while (budget-- > 0 && _posted.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    //Reported, not rethrown: one faulty completion must not take down the frame, or every
                    //window after it, with it.
                    _messages.Add("[" + DateTime.Now.ToLongTimeString() + "] EXCEPTION: " + e.Message);
                    Debug.LogException(e);
                }
            }
        }

        private void Update()
        {
            DrainMainThreadQueues();
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
