Command line program for running PREACT (which is the core of WUInity).

    PREACT <file.wui> [<numberOfRuns> [<batchSize> [<simulationIndexOffset>]]]

- file.wui: the scenario to run. It must load with nothing critical outstanding.
- numberOfRuns (optional, default 1): the maximum number of simulations to run one after another. Fewer are run
  when convergence is met (less than 2% change in the average RSET for 10 simulations in a row by default).
- batchSize: accepted for compatibility and ignored. Runs in one process are always serial (SUMO allows one
  instance per process); start several PREACT processes for parallelism, as the campaign tools do.
- simulationIndexOffset (optional, default 0): the index of the first simulation, used in output file names.

Exit code: 0 = every simulation ran to the end and no error was reported; 1 = nothing was run (bad arguments,
a scenario that does not load or is incomplete); 2 = a simulation stopped on an error, or an error was reported
during the run (for example k-PERIL refusing to compute a trigger boundary).

Linux: libgdal.so.36 (GDAL 3.10) must be findable by the dynamic loader (LD_LIBRARY_PATH); the wrappers under
Runtimes/Native are found by the engine itself. For traffic, SUMO_HOME must point at a SUMO 1.22 build or install
whose bin (or lib) holds libsumocs.
