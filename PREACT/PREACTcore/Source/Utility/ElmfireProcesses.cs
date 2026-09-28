using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PREACT.Utility
{
    /// <summary>
    /// Every external process the ELMFIRE pipeline starts - ELMFIRE itself, WindNinja, and (from the campaign
    /// driver) PREACT.exe - so that stopping a run can stop them too.
    /// </summary>
    /// <remarks>
    /// Without this a cancelled run left its children behind: the GUI killed the campaign CLI and every ELMFIRE
    /// and PREACT it had started kept running, re-parented, for hours; a relaunch then started a second ELMFIRE in
    /// the same run directory while the orphan was still writing there. Killing is by process tree, because an
    /// MPI build of ELMFIRE on Windows can sit under hydra/mpiexec helpers.
    ///
    /// The cancel generation lets a caller tell "this process exited because it was killed by a cancel" from an
    /// ordinary failure, without a cancel poisoning runs started after it.
    /// </remarks>
    public static class ElmfireProcesses
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<Process> Running = new HashSet<Process>();
        private static long _generation;

        /// <summary>Incremented by every <see cref="KillAll"/>. Capture it before starting work, compare after.</summary>
        public static long Generation => Interlocked.Read(ref _generation);

        public static bool CancelledSince(long generation) => Generation != generation;

        /// <summary>Registers a started process. A process registered after a cancel is left alone by it.</summary>
        public static void Register(Process process)
        {
            if (process == null) return;
            lock (Sync) Running.Add(process);
        }

        /// <summary>
        /// Registers a process started for work that began at <paramref name="generation"/>, and kills it at once
        /// when a cancel came in since - after the work started but before its process existed, so
        /// <see cref="KillAll"/> could not have seen it. Returns false when it was killed.
        /// </summary>
        /// <remarks>
        /// Without this a stop pressed while a run was still preparing (writing the namelist, hashing inputs) was
        /// missed: the process started a moment later and ran to the end.
        /// </remarks>
        public static bool Register(Process process, long generation)
        {
            if (process == null) return false;
            lock (Sync) Running.Add(process);
            //Added before looking: a KillAll after this line sees the process, one before it changed the generation.
            if (CancelledSince(generation))
            {
                KillTree(process);
                return false;
            }
            return true;
        }

        public static void Unregister(Process process)
        {
            if (process == null) return;
            lock (Sync) Running.Remove(process);
        }

        /// <summary>How many registered processes are still alive.</summary>
        public static int Count
        {
            get
            {
                lock (Sync)
                {
                    int n = 0;
                    foreach (Process p in Running)
                    {
                        try { if (!p.HasExited) ++n; } catch { }
                    }
                    return n;
                }
            }
        }

        /// <summary>
        /// Kills every registered process and its descendants, and marks everything running now as cancelled.
        /// Returns how many trees were killed. Safe to call from any thread and more than once.
        /// </summary>
        public static int KillAll()
        {
            Interlocked.Increment(ref _generation);

            Process[] snapshot;
            lock (Sync)
            {
                snapshot = new Process[Running.Count];
                Running.CopyTo(snapshot);
            }

            int killed = 0;
            foreach (Process p in snapshot)
            {
                if (KillTree(p)) ++killed;
            }
            return killed;
        }

        /// <summary>
        /// Kills a process and everything it started. <c>Process.Kill(true)</c> where the runtime has it (.NET
        /// Core 3 and later); otherwise <c>taskkill /T /F</c> on Windows, which is what Unity's Mono needs, and
        /// the children by parent id then the process itself elsewhere.
        /// </summary>
        public static bool KillTree(Process process)
        {
            if (process == null) return false;

            try
            {
                if (process.HasExited) return false;
            }
            catch
            {
                return false;
            }

            try
            {
                MethodInfo killTree = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });
                if (killTree != null)
                {
                    killTree.Invoke(process, new object[] { true });
                    return true;
                }
            }
            catch
            {
                //fall through to the platform route
            }

            int pid;
            try { pid = process.Id; } catch { return false; }

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RunQuietly("taskkill", "/PID " + pid + " /T /F");
                }
                else
                {
                    //Children first, so none of them is re-parented before it can be found by its parent's id.
                    RunQuietly("pkill", "-KILL -P " + pid);
                }
            }
            catch
            {
                //the direct kill below is still worth attempting
            }

            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch
            {
            }

            return true;
        }

        private static void RunQuietly(string exe, string arguments)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (Process p = Process.Start(psi))
            {
                p?.WaitForExit(10000);
            }
        }
    }
}
