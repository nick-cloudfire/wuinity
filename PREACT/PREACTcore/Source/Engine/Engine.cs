//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using PREACT.Input;
using System.IO;
using System.Collections.Generic;
using System;
using System.Diagnostics;
using PREACT.Utility.Analysis;
using PREACT.Runtime;
using System.Threading.Tasks;
using PREACT.Math;
using PREACT.Output;
using System.Reflection;
using System.Runtime.InteropServices;


namespace PREACT
{    
    public class Engine
    {
        private static Engine _ENGINE; //used only for console messages
        private EngineOutput _engineOutput;
        private IExternalManager _externalManager;
        private Simulation[] _simulations;
        private Simulation _mainSimulation; //this one talks to any visualizer         
        private PREACTInput _input;
        private string _workingFile;
        private WorkingData _workingData;

        string _defaultWorkingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public Simulation Simulation { get => _mainSimulation; }
        public string WorkingFile { get => _workingFile; }
        public string WorkingFolder
        {
            get
            {
                if(_input != null)
                {
                    return _input.RootFolder;
                }
                else
                {
                    return _defaultWorkingDirectory;
                }
            }
        }
        public string OutputFolder
        {
            get
            {
                string path = Path.Combine(WorkingFolder, "_output");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                return path;
            }
        }
        public WorkingData WorkingData { get => _workingData; }

        public Engine(IExternalManager externalManager, bool mainEngine = true)
        {
            //Needed for proper reading of input files on all systems. The calling thread is set explicitly, and
            //the default is set for every thread created after this - the simulation's Task.Run, any worker, a
            //GUI background load - so a comma-decimal locale (el-GR, de-DE) cannot read "38.05" as 3805 on
            //some thread that happened not to be this one. The parsers also pass InvariantCulture themselves.
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            _engineOutput = new EngineOutput(this);
            _workingData = new WorkingData();
            _externalManager = externalManager;
            if(mainEngine)
            {
                _ENGINE = this;
            }

            SetupNativeLibraries();                  
        }

        string _projLibPath, _projDataPath, _sumoPath;
        public string ProjLibPath { get => _projLibPath; }
        public string ProjDataPath { get => _projDataPath; }
        /// <summary>SUMO's bin folder, or null when SUMO was not found. Read from SUMO_HOME first, then PATH.</summary>
        public string SumoPath { get => _sumoPath; }

        /// <summary>
        /// Makes the native runtimes under Runtimes/Native findable, locates SUMO and PROJ, and registers GDAL.
        /// </summary>
        /// <remarks>
        /// Windows: the runtime folders are <b>prepended to the process PATH</b>, keeping whatever PATH the
        /// process was started with. This used to replace it with the Machine PATH, which dropped the user's
        /// PATH and anything the launcher had set - for this process and for every child it starts (SUMO,
        /// netconvert, the GDAL tools, mpiexec, ELMFIRE).
        ///
        /// Linux/macOS: the loader reads LD_LIBRARY_PATH/DYLD_LIBRARY_PATH once at start-up, so setting it here
        /// cannot help this process; the folders are given to a DllImport resolver instead (on .NET; Unity's
        /// Mono keeps its own probing). The variable is still extended so child processes see the same set.
        /// Reading the Machine-scope variable is Windows-only - it is null everywhere else, and the old
        /// <c>.Split</c> on it is what stopped PREACT.exe from starting on Linux at all.
        /// </remarks>
        private void SetupNativeLibraries()
        {
            string root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Runtimes", "Native");
            //No Behave entry: BEHAVE has been removed entirely - both the native library, which was called
            //from nothing, and the managed port k-PERIL once used to derive its own rate of spread.
            //FOFEM is kept on purpose (v1 decision) although nothing calls it yet; see its folder.
            string fofem = Path.Combine(root, "FOFEM", "x64");
            string gdal = Path.Combine(root, "GDAL", "x64");
            string nfdrs4 = Path.Combine(root, "NFDRS4", "x64");
            string[] runtimeFolders = { fofem, gdal, nfdrs4 };

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isOsx = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string libraryVariable = isWindows ? "PATH" : isOsx ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";

            _sumoPath = FindSumoBinFolder();

            //Runtimes first, so the committed wrappers win over any other copy; SUMO last (Windows), so that
            //adding it can never change which gdal.dll the wraps pick up - that is decided by the order the user's
            //PATH already has, which is the configuration known to work.
            var prepend = new List<string>(runtimeFolders);
            var append = new List<string>();
            if (isWindows && !string.IsNullOrEmpty(_sumoPath))
            {
                append.Add(_sumoPath);
            }
            Environment.SetEnvironmentVariable(libraryVariable, ExtendSearchPath(Environment.GetEnvironmentVariable(libraryVariable), prepend, append));

            if (!isWindows)
            {
                var resolverFolders = new List<string>(runtimeFolders);
                if (!string.IsNullOrEmpty(_sumoPath))
                {
                    resolverFolders.Add(_sumoPath);
                    //libsumocs.so sits in SUMO's bin in a SUMO build tree and in lib/ in some packages.
                    resolverFolders.Add(Path.Combine(Path.GetDirectoryName(_sumoPath) ?? _sumoPath, "lib"));
                }
                NativeLibraries.Register(resolverFolders,
                    typeof(Engine).Assembly, typeof(OSGeo.GDAL.Gdal).Assembly, typeof(OSGeo.OGR.Ogr).Assembly, typeof(OSGeo.OSR.Osr).Assembly);
            }

            //now some GDAL/PROJ stuff. The process environment, which on Windows already holds the Machine and
            //User values it was started with; the Machine-only lookup this used to do returned null elsewhere and
            //then handed those nulls to PROJ.
            _projLibPath = Environment.GetEnvironmentVariable("PROJ_LIB");
            _projDataPath = Environment.GetEnvironmentVariable("PROJ_DATA");
            var projPaths = new List<string>();
            foreach (string candidate in new[] { _projDataPath, _projLibPath })
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !projPaths.Contains(candidate))
                {
                    projPaths.Add(candidate);
                }
            }
            if (projPaths.Count == 0 && !isWindows && Directory.Exists("/usr/share/proj"))
            {
                projPaths.Add("/usr/share/proj");
            }
            //Only when something was found: an empty list would replace PROJ's own compiled-in search path.
            if (projPaths.Count > 0)
            {
                OSGeo.OSR.Osr.SetPROJSearchPaths(projPaths.ToArray());
            }

            OSGeo.GDAL.Gdal.AllRegister();
            OSGeo.OGR.Ogr.RegisterAll();
        }

        /// <summary>
        /// SUMO's bin folder: SUMO_HOME/bin when that exists, else the first PATH entry holding the sumo
        /// executable, else the first PATH entry that looks like a SUMO bin folder. Null when there is none.
        /// </summary>
        /// <remarks>
        /// This used to accept only a Machine-PATH entry containing both "Sumo" and "bin", case-sensitively - so a
        /// user-PATH install, "sumo" in lower case, or any Linux install was never found.
        /// </remarks>
        private static string FindSumoBinFolder()
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string executable = isWindows ? "sumo.exe" : "sumo";

            string sumoHome = Environment.GetEnvironmentVariable("SUMO_HOME");
            if (!string.IsNullOrWhiteSpace(sumoHome))
            {
                string bin = Path.Combine(sumoHome.Trim(), "bin");
                if (Directory.Exists(bin))
                {
                    return bin;
                }
            }

            string[] entries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
            foreach (string entry in entries)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(entry) && File.Exists(Path.Combine(entry.Trim(), executable)))
                    {
                        return entry.Trim();
                    }
                }
                catch (ArgumentException)
                {
                    //an unusable PATH entry is not worth failing start-up over
                }
            }

            foreach (string entry in entries)
            {
                string lower = entry.ToLowerInvariant();
                if (lower.Contains("sumo") && lower.Contains("bin") && Directory.Exists(entry.Trim()))
                {
                    return entry.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// <paramref name="current"/> with <paramref name="prepend"/> in front and <paramref name="append"/> at the
        /// end, skipping folders already present so repeated engines do not grow it.
        /// </summary>
        private static string ExtendSearchPath(string current, List<string> prepend, List<string> append)
        {
            char separator = Path.PathSeparator;
            var existing = new List<string>();
            foreach (string entry in (current ?? string.Empty).Split(separator))
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    existing.Add(entry);
                }
            }

            var result = new List<string>();
            foreach (string folder in prepend)
            {
                if (!existing.Contains(folder) && !result.Contains(folder))
                {
                    result.Add(folder);
                }
            }
            result.AddRange(existing);
            foreach (string folder in append)
            {
                if (!result.Contains(folder))
                {
                    result.Add(folder);
                }
            }

            return string.Join(separator.ToString(), result);
        }

        /// <summary>
        /// Runs <paramref name="engineTask"/>'s simulations one after another, and always ends by calling the
        /// external manager's <c>SimulationsFinished</c> - whatever happened.
        /// </summary>
        /// <remarks>
        /// Returns a Task so callers can wait for the run. Callers that call it fire-and-forget (the Unity manager)
        /// are safe too: nothing escapes it. An exception used to be rethrown out of here into an un-awaited task,
        /// so it was lost and SimulationsFinished was never called - the GUI stayed "running" until restarted.
        ///
        /// Refuses to start when the scenario checklist has a critical item outstanding (see
        /// <see cref="PREACTInput.RequirementsMet"/>; a GUI should call <see cref="PREACTInput.Revalidate"/> after
        /// editing). Only serial execution remains: the in-process Parallel mode could not work (SUMO allows one
        /// instance per process, and the batch count had an operator-precedence bug), and the campaign tools
        /// already run realizations in parallel as separate PREACT processes.
        /// </remarks>
        public async Task RunSimulations(EngineTask engineTask)
        {
            _runErrors = 0;
            _lastRunSucceeded = false;
            try
            {
                if(_input == null)
                {
                    Message(null, LogType.InputError, "No input has been set, aborting.");
                    ++_runErrors;
                    return;
                }

                if (!PREACTInput.RequirementsMet)
                {
                    var missing = new List<string>();
                    foreach (PREACTInput.InputRequirement requirement in PREACTInput.Requirements)
                    {
                        if (requirement.Critical) missing.Add(requirement.ToString());
                    }
                    Message(null, LogType.InputError, "The scenario is not complete enough to run; still required: "
                        + string.Join("; ", missing) + ". See the scenario checklist.");
                    ++_runErrors;
                    return;
                }

                Message(null, LogType.Log, "Will try to run a max total of " + engineTask.NumberOfRuns + " simulations unless aborted early (convergence met, user stoppage or simulation error)." );
                await Task.Run(() => RunSimulationsSerial(engineTask));
                _lastRunSucceeded = _runErrors == 0;
            }
            catch (Exception e)
            {
                ++_runErrors;
                Message(null, LogType.Exception, "The run stopped on an unexpected error: " + e);
            }
            finally
            {
                if(_externalManager != null)
                {
                    try
                    {
                        _externalManager.SimulationsFinished();
                    }
                    catch (Exception e)
                    {
                        Message(null, LogType.Exception, "SimulationsFinished threw: " + e.Message);
                    }
                }
            }
        }

        private volatile bool _stopSimulations = false;
        private int _runErrors;
        private bool _lastRunSucceeded;

        /// <summary>
        /// Whether the last <see cref="RunSimulations"/> ran and completed without any simulation stopping on an
        /// error or any error being reported. PREACT.exe turns this into its exit code.
        /// </summary>
        public bool LastRunSucceeded { get => _lastRunSucceeded; }

        /// <summary>Errors (SimulationError/InputError/Exception) reported during the last run.</summary>
        public int LastRunErrorCount { get => _runErrors; }

        private void RunSimulationsSerial(EngineTask engineTask)
        {
            PreSimulations(engineTask);

            //Created before any module starts: a SUMO configuration may log into it (the Roxborough example's
            //writes ../_output/log.txt), and SUMO refuses to start when the folder is not there yet - which it
            //never is on a scenario's first run.
            string outputFolder = OutputFolder;
            Message(null, LogType.Log, "Output is written to " + outputFolder);

            _simulations = new Simulation[1];
            for (int i = 0; i < engineTask.NumberOfRuns; ++i)
            {
                int simulationIndex = i + engineTask.SimulationIndexOffset;
                _mainSimulation = new Simulation(this, _input, simulationIndex);
                _simulations[0] = _mainSimulation; 
                _mainSimulation.Run();

                if (_mainSimulation.StoppedDueToError)
                {
                    ++_runErrors;
                }

                if (_mainSimulation.Evacuation.TrafficModule != null)
                {
                    CollectSimulationStatistics(_mainSimulation.Output.GetTrafficArrivalData(), simulationIndex,engineTask);
                }                
                if (_stopSimulations)
                {
                    break;
                }
            }

            PostSimulations(engineTask);
        }

        private void PreSimulations(EngineTask engineTask)
        {
            _stopSimulations = false;
            //Per run of RunSimulations: a second run in the same session used to average across both.
            cumulativeTotalEvacTime = 0.0;
            convergedInSequence = 0;

            if (trafficArrivalDataCollection != null)
            {
                trafficArrivalDataCollection.Clear();
            }
            else
            {
                trafficArrivalDataCollection = new List<List<double>>();
            }      
        }  
        
        private void PostSimulations(EngineTask engineTask)
        {
            //save functional analysis
            int actualRuns = trafficArrivalDataCollection.Count;
            if (actualRuns > 0)
            {
                double[] averageCurve = FunctionalAnalysis.CalculateAverageCurve(trafficArrivalDataCollection, FunctionalAnalysis.DimensionScalingMode.Average);
                _engineOutput.SaveAverageCurve(averageCurve);

                if (engineTask.StopAfterConverging && convergedInSequence >= engineTask.ConvergenceMinSequence)
                {
                    Message(null, LogType.Log, "Average total evacuation time: " + cumulativeTotalEvacTime / actualRuns + " seconds, ran " + actualRuns + " simulations before converging according to user set criteria.");

                }
                else
                {
                    Message(null, LogType.Log, "Average total evacuation time: " + cumulativeTotalEvacTime / actualRuns + " seconds, ran " + actualRuns + " simulation/s.");
                }
            }
            else
            {
                Message(null, LogType.Log, "No completed traffic simulations were performed, cannot analyse statistics.");
            }

            List<string> log;
            lock (_consoleLog)
            {
                log = new List<string>(_consoleLog);
            }
            SimulationOutput.SaveLogToDisk(log, Path.Combine(OutputFolder, _input.Simulation.Name + ".log"));
        }

        double cumulativeTotalEvacTime = 0.0;
        int convergedInSequence = 0;
        List<List<double>> trafficArrivalDataCollection;
        /// <summary>
        /// Called after each simulation to see whether the average evacuation time has converged and the
        /// remaining runs can be skipped.
        /// </summary>
        /// <remarks>
        /// Converged means the running average of RSET (last arrival) moved by less than
        /// <see cref="EngineTask.ConvergenceMaxDifference"/> (relative, either direction) for
        /// <see cref="EngineTask.ConvergenceMinSequence"/> runs in a row. The test used to be one-sided (a falling
        /// average always passed) and a failure reset the streak to 1 instead of 0.
        /// </remarks>
        private void CollectSimulationStatistics(List<double> arrivalData, int simulationIndex, EngineTask engineTask)
        {
            if(arrivalData.Count < 1)
            {
                return;
            }

            trafficArrivalDataCollection.Add(arrivalData);
            double RSET = arrivalData[arrivalData.Count - 1];

            int resultCount = trafficArrivalDataCollection.Count;
            double pastAverage = resultCount > 1 ? cumulativeTotalEvacTime / (resultCount - 1) : 0.0;
            cumulativeTotalEvacTime += RSET;

            //need at least 2 simulations to have valid average
            if (resultCount < 2)
            {
                return;
            }

            Message(null, LogType.Log, "Evaluating convergence criteria...");
            double currentAverage = cumulativeTotalEvacTime / resultCount;
            double convergenceCriteria = currentAverage != 0.0 ? System.Math.Abs(currentAverage - pastAverage) / System.Math.Abs(currentAverage) : 0.0;
            if (convergenceCriteria < engineTask.ConvergenceMaxDifference)
            {
                ++convergedInSequence;
                Message(null, LogType.Log, "RSET for simulation " + simulationIndex + " was within convergence criteria, total runs in convergence sequence: " + convergedInSequence);
                if (!_stopSimulations && engineTask.StopAfterConverging && convergedInSequence >= engineTask.ConvergenceMinSequence)
                {
                    Message(null, LogType.Log, "Convergence criteria have been met, stopping after this simulation.");
                    _stopSimulations = true;
                }
            }
            else
            {
                Message(null, LogType.Log, "Convergence has not been met.");
                convergedInSequence = 0;
            }
        }

        public void SetInput(PREACTInput input, string filePath)
        {
            _input = input;
            _workingFile = filePath;
            UpdateExternalManager(_input);
        }

        public void LoadInputFromFile(string filePath, out bool success)
        {
            LoadInputFromFile(filePath, out success, true);
        }

        /// <summary>
        /// Loads a scenario. With <paramref name="acceptIncomplete"/> the input is taken on even when
        /// items are still outstanding, so a half-built scenario can be opened, worked on and saved -
        /// which is the normal way one gets built. <see cref="RunSimulations"/> refuses to start while the
        /// checklist has a critical item, so nothing runs on a scenario with holes in it.
        /// </summary>
        public void LoadInputFromFile(string filePath, out bool success, bool acceptIncomplete)
        {
            //The log written beside a run's output (<Name>.log) covers this load and everything after it. It
            //used to be cleared when the run started instead, which dropped the load messages - the checklist
            //items and path corrections - from the one file that records the run.
            lock (_consoleLog)
            {
                _consoleLog.Clear();
            }
            PREACTInput input = PREACTInput.LoadFromDisk(filePath, out success);

            if (input == null)
            {
                return;
            }

            if (success || acceptIncomplete)
            {
                _input = input;
                _workingFile = filePath;
                UpdateExternalManager(_input);
            }
        }

        private void UpdateExternalManager(PREACTInput input)
        {
            if (_externalManager != null)
            {
                _externalManager.UpdateInput(input);
            }
        }

        public void UpdateEvacuationDestinations(Simulation simulation, List<Evacuation.EvacuationDestination> destinations)
        {
            if (_externalManager != null && simulation == _mainSimulation)
            {
                _externalManager.UpdateDestinations(destinations);
            }
        }


        public enum LogType { Log, Warning, SimulationError, InputError, Event, Exception, Debug };
        private readonly List<string> _consoleLog = new List<string>();

        /// <summary>
        /// Receives all the information from a WUINITY session, used by GUI. Safe to call from any thread.
        /// </summary>
        /// <remarks>
        /// A <see cref="LogType.SimulationError"/> stops the simulation it belongs to: <paramref name="simulation"/>
        /// when given, else the simulation running on the calling thread, else nothing. It used to stop every
        /// simulation whoever reported it, so a GUI-side problem (an invalid Mapbox token, a painter mode, a failed
        /// save) killed a run in progress. Messages from outside a simulation should use InputError or Warning.
        /// </remarks>
        public static void Message(Simulation? simulation, LogType logType, string message)
        {
            Engine engine = _ENGINE;
            if (engine == null)
            {
                return;
            }

            Simulation owner = simulation ?? Simulation.RunningOnThisThread;
            if (simulation != null)
            {
                if(simulation.State == Simulation.SimulationState.Running)
                {
                    //The simulation's own clock, not the wall clock - and stated unambiguously, because it
                    //reads as one.
                    message = $"[Simulation# {simulation.SimulationIndex}, sim time "
                              + simulation.Time.CurrentDateTime.ToString("yyyy-MM-dd HH:mm:ss",
                                  System.Globalization.CultureInfo.InvariantCulture)
                              + $" ({simulation.Time.SimulationTime:F0} s in)] " + message;
                }
                else
                {
                    message = $"[Simulation# { simulation.SimulationIndex}] {message}";
                }
                
            }

            if (logType == LogType.Warning)
            {
                message = "WARNING: " + message;
            }
            else if (logType == LogType.SimulationError || logType == LogType.InputError)
            {
                message = "ERROR: " + message;
            }
            else if (logType == LogType.Event)
            {
                message = "EVENT: " + message;
            }
            else if (logType == LogType.Exception)
            {
                message = "EXCEPTION: " + message;
            }
            else if(logType == LogType.Debug)
            {
                message = "DEBUG: " + message;
            }
            message = "[" + DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "] " + message;

            lock (engine._consoleLog)
            {
                engine._consoleLog.Add(message);
            }

            if (owner != null && (logType == LogType.SimulationError || logType == LogType.Exception))
            {
                System.Threading.Interlocked.Increment(ref engine._runErrors);
            }

            if(engine._externalManager != null)
            {
                engine._externalManager.NewLogMessage(message);
            }

            if (logType == LogType.SimulationError && owner != null)
            {
                owner.Stop("Critical error in simulation, aborting.", true);
            }
        }

        /// <summary>
        /// Stops the run: no further simulation is started, the current one stops at its next step, and every
        /// ELMFIRE (and WindNinja) process tree this process started is killed.
        /// </summary>
        /// <remarks>
        /// A simulation spends its set-up inside ELMFIRE, which is a separate process that can take hours; the
        /// stop flag alone is only looked at between steps, so a stop during the fire waited for ELMFIRE to
        /// finish, and quitting left it running in the background. Killing it makes the run return at once
        /// with "ELMFIRE was stopped" (contract C3). Safe to call when nothing is running.
        /// </remarks>
        public void CloseSimulations(bool stoppedDueToError)
        {
            _stopSimulations = true;
            Simulation[] simulations = _simulations;
            if(simulations != null)
            {
                for (int i = 0; i < simulations.Length; ++i)
                {
                    if (simulations[i] != null)
                    {
                        if (stoppedDueToError)
                        {
                            simulations[i].Stop("Critical error in simulation, aborting.", true);
                        }
                        else
                        {
                            simulations[i].Stop("User has requested closing.", false);
                        }
                    }
                }
            }

            //After the stop flags, so the run that loses its fire already knows it was asked to stop.
            Utility.ElmfireRunner.CancelAll();
        }
    }
}

