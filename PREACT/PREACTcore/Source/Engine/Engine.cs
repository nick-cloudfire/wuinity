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
            //needed for proper reading of input files on all systems
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
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
        public string SumoPath { get => _sumoPath; }


        private void SetupNativeLibraries()
        {
            string root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Runtimes", "Native");
            //No Behave entry: BEHAVE has been removed entirely - both the native library, which was called
            //from nothing, and the managed port k-PERIL once used to derive its own rate of spread.
            string fofem = Path.Combine(root, "FOFEM", "x64");
            string gdal = Path.Combine(root, "GDAL", "x64");
            string nfdrs4 = Path.Combine(root, "NFDRS4", "x64");

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string NEXT = isWindows ? ";" : ":";
            string runtimes = fofem + NEXT + gdal + NEXT + nfdrs4;

            string machineEnvirtonmentVariables;
            if (isWindows)
            {
                machineEnvirtonmentVariables = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
                Environment.SetEnvironmentVariable("PATH", runtimes + ";" + machineEnvirtonmentVariables);
            }
            else if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                machineEnvirtonmentVariables = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH", EnvironmentVariableTarget.Machine);
                Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", runtimes + ":" + machineEnvirtonmentVariables);
            }
            else if(RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                machineEnvirtonmentVariables = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH", EnvironmentVariableTarget.Machine) ?? "";
                Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH", runtimes + (string.IsNullOrEmpty(machineEnvirtonmentVariables) ? "" : ":" + machineEnvirtonmentVariables));
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            string[] variables = machineEnvirtonmentVariables.Split(NEXT);
            for(int i = 0; i < variables.Length; ++i)
            {
                if (variables[i].Contains("Sumo") && variables[i].Contains("bin"))
                {
                    _sumoPath = variables[i];
                    break;
                }
            }

            //now some GDAL/PROJ stuff
            _projLibPath = Environment.GetEnvironmentVariable("PROJ_LIB", EnvironmentVariableTarget.Machine);
            _projDataPath = Environment.GetEnvironmentVariable("PROJ_DATA", EnvironmentVariableTarget.Machine);
            //Engine.Message(null, LogType.Debug, $"PROJ_LIB variable is: {projLib}");
            //Engine.Message(null, LogType.Debug, $"PROJ_DATA variable is: {projData}");
            //OSGeo.GDAL.Gdal.SetConfigOption("PROJ_LIB", projLib); //should not be needed
            //OSGeo.GDAL.Gdal.SetConfigOption("PROJ_DATA", projData);
            OSGeo.OSR.Osr.SetPROJSearchPaths(new string[] { _projLibPath, _projDataPath });

            try
            {
                OSGeo.GDAL.Gdal.AllRegister();
            }
            catch (Exception)
            {
                throw;
            }

            try
            {
                OSGeo.OGR.Ogr.RegisterAll();
            }
            catch (Exception)
            {
                throw;
            }
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
                    if(double.TryParse(data[i], out value))
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

