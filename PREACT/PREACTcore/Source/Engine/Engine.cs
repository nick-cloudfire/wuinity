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
        private DataStatus _dataStatus;
        private string _workingFile;
        private WorkingData _workingData;

        string _defaultWorkingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public Simulation Simulation { get => _mainSimulation; }
        public DataStatus DataStatus { get => _dataStatus; }        
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
            _dataStatus = new DataStatus();
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

        // Returns a Task rather than being 'async void' so callers can actually wait for the run
        // and observe its exceptions. As async void it was fire-and-forget: PREACT.exe could only
        // poll a flag set by the SimulationsFinished callback, and any failure in here surfaced
        // nowhere, leaving the process spinning forever on a run that had already finished or
        // already died. Callers that genuinely want fire-and-forget (the Unity manager) can still
        // call it without awaiting.
        public async Task RunSimulations(EngineTask engineTask, int startIndexOffset = 0)
        {
            if(_input == null)
            {
                Message(null, LogType.SimulationError, "No input has been set, aborting.");
                return;
            }

            Message(null, LogType.Log, "Will try to run a max total of " + engineTask.NumberOfRuns + " simulations unless aborted early (convergence met, user stoppage or simulation error)." );
            _consoleLog.Clear();

            try
            {    
                Task task;
                if(engineTask.Execution == EngineTask.ExecutionMode.Parallel)
                {

                    Message(null, LogType.Log, "Starting simulations/s in parallel mode.");
                    task = Task.Run(() => RunSimulationsParallel(engineTask)); 
                    
                }
                else if(engineTask.Execution == EngineTask.ExecutionMode.ParallelProcess)
                {
                    Message(null, LogType.Log, "Starting simulations/s in parallel process mode.");
                    task = Task.Run(() => RunSimulationsParallelProcess(engineTask));
                }
                else
                {
                    Message(null, LogType.Log, "Starting simulations/s in serial mode.");
                    task = Task.Run(() => RunSimulationsSerial(engineTask));
                }
                await task;

                if(_externalManager != null)
                {
                    _externalManager.SimulationsFinished();
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        bool _stopSimulations = false;
        private void RunSimulationsSerial(EngineTask engineTask)
        {
            PreSimulations(engineTask);

            _simulations = new Simulation[1];
            for (int i = 0; i < engineTask.NumberOfRuns; ++i)
            {
                int simulationIndex = i + engineTask.SimulationIndexOffset;
                _mainSimulation = new Simulation(this, _input, simulationIndex);
                _simulations[0] = _mainSimulation; 
                SetMainSimulation(simulationIndex);
                //only run wui show in serial mode
                _mainSimulation.Run();

                if (_mainSimulation.Evacuation.TrafficModule != null)
                {
                    CollectSimulationStatistics(_mainSimulation.Output.GetTrafficArrivalData(), simulationIndex,engineTask);
                }                
                if (_stopSimulations)
                {
                    break;
                }
            }

            PostSimulations();
        }

        private void RunSimulationsParallelProcess(EngineTask engineTask)
        {
            PreSimulations(engineTask);

            //we run them in batches as running everything at once will likely overload the CPU cores, and we might waste a lot of processing power since we could terminate early if we reach convergence
            int batches = engineTask.NumberOfRuns / engineTask.BatchSize + (engineTask.NumberOfRuns % engineTask.BatchSize > 0 ? 1 : 0);
            Message(null, LogType.Log, "Running a max total of " + batches +" batches with a batch size (parallel simulations) of " + engineTask.BatchSize);
            int simulationIndex = engineTask.SimulationIndexOffset;
            for (int i = 0; i < batches; ++i)
            {
                int startIndex = simulationIndex;
                int endIndex = Mathf.Min(startIndex + engineTask.BatchSize, engineTask.NumberOfRuns);
                int simulationCount = endIndex - startIndex;
                Message(null, LogType.Log, "Starting batch number " + i + " which will run " + simulationCount + " simulations.");
                Task[] tasks = new Task[simulationCount];
                string[] outputFilePaths = new string[simulationCount - 1]; 

                for (int j = 0; j < simulationCount; ++j)
                {                    
                    //run first one in this process
                    if (j == 0)
                    {
                        _mainSimulation = new Simulation(this, _input, simulationIndex);
                        tasks[j] = Task.Run(() => _mainSimulation.Run());
                    }
                    //run the rest as new processes so that SUMO works
                    else
                    {
                        try
                        {
                            Message(null, LogType.Log, "Starting PREACT process...");
                            ProcessStartInfo preactRun = new ProcessStartInfo();
                            preactRun.FileName = "preact.exe";
                            outputFilePaths[j - 1] = Path.Combine(OutputFolder, _input.Simulation.Name + "_" + simulationIndex + "_arrivalData.csv");
                            preactRun.Arguments = WorkingFile + " " + 1 + " " + 1 + " " + simulationIndex;//filePath, number of runs, batchsize, simulation index offset
                            preactRun.CreateNoWindow = false;
                            preactRun.UseShellExecute = true;
                            tasks[j] = Task.Run(() => Process.Start(preactRun).WaitForExit());
                        }
                        catch (Exception)
                        {
                            throw;
                        }
                        
                    }
                    ++simulationIndex;
                    //TODO: ugly, but GDAL seems to grab files and give sharing violation on read
                    System.Threading.Thread.Sleep(2000);
                }

                Task.WaitAll(tasks);

                for (int j = 0; j < simulationCount; ++j)
                {
                    if (j == 0)
                    {
                        CollectSimulationStatistics(_mainSimulation.Output.GetTrafficArrivalData(), startIndex + j , engineTask);                      
                    }
                    else
                    {
                        bool success;
                        List<double> dataFromDisk = ParseArrivalData(outputFilePaths[j - 1], out success);
                        if (success)
                        {
                            CollectSimulationStatistics(dataFromDisk, startIndex + j, engineTask);
                        }                        
                    }
                }

                if (_stopSimulations)
                {
                    break;
                }
            }

            PostSimulations();
        }

        private List<double> ParseArrivalData(string filePath, out bool success)
        {
            List<double> result = new List<double>(); 
            success = false;

            if(File.Exists(filePath))
            {
                string[] data = File.ReadAllLines(filePath);

                //skip last line, empty
                for(int i = 0; i < data.Length - 1; ++i)
                {
                    double value;
                    if(double.TryParse(data[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                    {
                        result.Add(value);
                    }
                }

                success = true;
            }
            
            if(!success)
            {
                Message(null, LogType.Warning, "Could not read arrival data from " + filePath + ", skipping data from simulation.");
            }
            else
            {
                Message(null, LogType.Log, "Success in reading arrival data from " + filePath + ".");
            }

            return result;
        }
                       
        private void RunSimulationsParallel(EngineTask engineTask)
        {
            PreSimulations(engineTask);

            //Currently this will not work as SUMO can only run one instance per process, need to find workaround
            int batches = engineTask.NumberOfRuns / engineTask.BatchSize + engineTask.NumberOfRuns % engineTask.BatchSize > 0 ? 1 : 0;
            int simulationIndex = engineTask.SimulationIndexOffset;
            for (int i = 0; i < batches; ++i)
            {
                int startIndex = i * engineTask.BatchSize;
                int endIndex = Mathf.Min(startIndex + engineTask.BatchSize, engineTask.NumberOfRuns);
                Parallel.For(startIndex, endIndex, index =>
                {
                    try
                    {                        
                        _simulations[index].Run();
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                    ++simulationIndex;
                });

                for(int j = startIndex; j < endIndex; ++j)
                {
                    CollectSimulationStatistics(_simulations[j].Output.GetTrafficArrivalData(), simulationIndex, engineTask);                  
                }
                if(_stopSimulations)
                {
                    break;
                }
            }                

            PostSimulations();
        }

        private void PreSimulations(EngineTask engineTask)
        {
            _stopSimulations = false;

            if (trafficArrivalDataCollection != null)
            {
                trafficArrivalDataCollection.Clear();
            }
            else
            {
                trafficArrivalDataCollection = new List<List<double>>();
            }      
        }  
        
        public void SetMainSimulation(int simulationIndex)
        {
            if (simulationIndex >= 0 && simulationIndex < _simulations.Length)
            {
                _mainSimulation = _simulations[simulationIndex];
            }
        }
        
        private void PostSimulations()
        {
            //save functional analysis
            int actualRuns = trafficArrivalDataCollection.Count;
            if (actualRuns > 0)
            {
                double[] averageCurve = FunctionalAnalysis.CalculateAverageCurve(trafficArrivalDataCollection, FunctionalAnalysis.DimensionScalingMode.Average);
                _engineOutput.SaveAverageCurve(averageCurve);
                //plot results
                double[] xData = new double[averageCurve.Length];
                double[] yData = new double[averageCurve.Length];
                for (int i = 0; i < averageCurve.Length; i++)
                {
                    xData[i] = averageCurve[i] / 3600.0f;
                    yData[i] = i + 1;
                }
                _engineOutput.CreatePlotData(xData, yData);

                if (convergedInSequence >= 10)
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

            SimulationOutput.SaveLogToDisk(_consoleLog, Path.Combine(OutputFolder, _input.Simulation.Name + ".log"));
        }

        double cumulativeTotalEvacTime = 0.0f;
        int convergedInSequence = 0;
        List<List<double>> trafficArrivalDataCollection;
        /// <summary>
        /// Each simulation calls this function when it is done to see if evacuation time has vonverged and simulations should be stopped.
        /// </summary>
        /// <param name="simulation"></param>
        private void CollectSimulationStatistics(List<double> arrivalData, int simulationIndex, EngineTask engineTask)
        {
            if(arrivalData.Count < 1)
            {
                return;
            }

            trafficArrivalDataCollection.Add(arrivalData);
            double RSET = arrivalData[arrivalData.Count - 1];

            int resultCount = trafficArrivalDataCollection.Count;
            //need at least 2 simulations to have valid average
            if (resultCount > 1)
            {
                Message(null, LogType.Log, "Evaluating convergence criteria...");
                double pastAverage = cumulativeTotalEvacTime / (resultCount - 1);
                
                cumulativeTotalEvacTime += RSET;
                double currentAverage = cumulativeTotalEvacTime / resultCount;
                double convergenceCriteria = (currentAverage - pastAverage) / currentAverage;
                //if convergence met we can stop
                if (convergenceCriteria < engineTask.ConvergenceMaxDifference)
                {
                    ++convergedInSequence;
                    Message(null, LogType.Log, "RSET for simulation " + simulationIndex + " was within convergence criteria, total runs in convergence sequence: " + convergedInSequence);
                    //we are done
                    if (!_stopSimulations && engineTask.StopAfterConverging && convergedInSequence >= engineTask.ConvergenceMinSequence)
                    {
                        Message(null, LogType.Log, "Convergence critiera has been met, shutting down after this batch finishes.");
                        _stopSimulations = true; //needed for serial run
                        CloseSimulations(false); //needed for parallel run
                    }
                }
                else
                {
                    Message(null, LogType.Log, "Convergence has not been met.");
                    convergedInSequence = 1;
                }
            }
            else
            {
                cumulativeTotalEvacTime += RSET;
            }
        }

        public void SetInput(PREACTInput input, string filePath)
        {
            _dataStatus.HaveInput = true;
            _input = input;
            _workingFile = filePath;
            _dataStatus.Reset();
            _dataStatus.HaveInput = true;
            UpdateExternalManager(_input);
        }

        public void LoadInputFromFile(string filePath, out bool success)
        {
            LoadInputFromFile(filePath, out success, true);
        }

        /// <summary>
        /// Loads a scenario. With <paramref name="acceptIncomplete"/> the input is taken on even when
        /// items are still outstanding, so a half-built scenario can be opened, worked on and saved -
        /// which is the normal way one gets built. HaveInput still tracks completeness, so nothing
        /// starts a simulation on a scenario with holes in it.
        /// </summary>
        public void LoadInputFromFile(string filePath, out bool success, bool acceptIncomplete)
        {
            _dataStatus.HaveInput = false;
            PREACTInput input = PREACTInput.LoadFromDisk(filePath, out success);

            if (input == null)
            {
                return;
            }

            if (success || acceptIncomplete)
            {
                _input = input;
                _workingFile = filePath;
                _dataStatus.Reset();
                _dataStatus.HaveInput = success;
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
        private List<string> _consoleLog = new List<string>();
        /// <summary>
        /// Receives all the information from a WUINITY session, used by GUI.
        /// </summary>
        /// <param name="message"></param>
        public static void Message(Simulation? simulation, LogType logType, string message)
        {
            if (_ENGINE == null)
            {
                return;
            }

            if (simulation != null)
            {
                if(simulation.State == Simulation.SimulationState.Running)
                {
                    //The simulation's own clock, not the wall clock - and stated unambiguously, because it
                    //reads as one. It used to be CurrentDateTime.ToString() with an "s" stuck on the end, so
                    //a simulated date came out as "06/29/2026 12:35:53s": a trailing unit that made a date
                    //look like a duration, in a culture-dependent format sitting next to the console's own
                    //wall-clock stamp. Anyone reading it took it for the time of day the message was logged.
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
                message = "!!!DEBUG!!!: " + message;
            }
            /*else
            {
                message = "LOG: " + message;
            }*/
            message = "[" + DateTime.Now.ToLongTimeString() + "] " + message;

            _ENGINE._consoleLog.Add(message);

            if(_ENGINE._externalManager != null)
            {
                _ENGINE._externalManager.NewLogMessage(message);
            }

            if (logType == LogType.SimulationError)
            {
                _ENGINE.CloseSimulations(true);
            }           
        }

        public void PauseSimulations()
        {
            for (int i = 0; i < _simulations.Length; ++i)
            {
                _simulations[i].SetPause(true);
            }
        }

        public void UnpauseSimulations()
        {
            for (int i = 0; i < _simulations.Length; ++i)
            {
                _simulations[i].SetPause(false);
            }
        }

        public void CloseSimulations(bool stoppedDueToError)
        {
            _stopSimulations = true;           
            if(_simulations != null)
            {
                for (int i = 0; i < _simulations.Length; ++i)
                {
                    if (_simulations[i] != null)
                    {
                        if (stoppedDueToError)
                        {
                            _simulations[i].Stop("Critical error in simulation, aborting.", true);
                        }
                        else
                        {
                            _simulations[i].Stop("User has requested closing.", false);
                        }
                    }
                }
            }            
        } 
    }
}

