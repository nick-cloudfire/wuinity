using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// Makes sure every ELMFIRE, WindNinja and PREACT.exe process the campaign starts dies with it.
    /// </summary>
    /// <remarks>
    /// Cancelling used to kill only the CLI: its children were re-parented and kept running for hours, and a
    /// relaunch started a second ELMFIRE in the same realization directory while the orphan was still writing
    /// there. Four ways out are covered:
    /// <list type="bullet">
    /// <item>Ctrl+C / Ctrl+Break and SIGTERM (ProcessExit) kill the children before the CLI exits.</item>
    /// <item>With <c>--cancel-on-stdin-close</c>, stdin reaching end-of-file is a cancel. The GUI launches the CLI
    /// with a pipe on stdin and cancels by closing it - and when the GUI itself goes away, so does the pipe.</item>
    /// <item>On Windows the CLI puts itself in a Job Object with KILL_ON_JOB_CLOSE. Children inherit the job, so
    /// even a hard kill of the CLI (TerminateProcess, Task Manager, Unity's Process.Kill) takes every child with
    /// it: the job's last handle closes with the CLI's process.</item>
    /// </list>
    /// A SIGKILL of the CLI on Linux still leaves children behind; nothing in-process can prevent that.
    /// </remarks>
    internal static class ChildProcessGuard
    {
        private static int _cancelled;
        private static readonly ManualResetEventSlim CancelEvent = new ManualResetEventSlim(false);

        /// <summary>Set once a cancel has been requested; realizations stop at their next stage.</summary>
        public static bool IsCancelled => Volatile.Read(ref _cancelled) != 0;

        public static WaitHandle CancelHandle => CancelEvent.WaitHandle;

        public static void Install(bool cancelOnStdinClose)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true; //let the campaign unwind and report; the children are killed now
                Cancel("interrupted");
            };

            AppDomain.CurrentDomain.ProcessExit += (_, __) => ElmfireProcesses.KillAll();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryJoinKillOnCloseJob();
            }

            if (cancelOnStdinClose)
            {
                var watcher = new Thread(() =>
                {
                    try
                    {
                        while (Console.In.ReadLine() != null)
                        {
                            //anything written is ignored; only the end of the stream means something
                        }
                    }
                    catch
                    {
                    }
                    Cancel("stdin closed");
                })
                {
                    IsBackground = true,
                    Name = "stdin-cancel",
                };
                watcher.Start();
            }
        }

        /// <summary>Requests a cancel: kills every child tree now and makes the campaign stop.</summary>
        public static void Cancel(string why)
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
            Console.Error.WriteLine($"Cancelling ({why}): stopping every ELMFIRE, WindNinja and PREACT process this campaign started.");
            int killed = ElmfireProcesses.KillAll();
            if (killed > 0) Console.Error.WriteLine($"  killed {killed} process tree(s).");
            CancelEvent.Set();
        }

        // ------------------------------------------------------------------ Windows job object

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationStruct
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoType,
            ref JobObjectExtendedLimitInformationStruct info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        /// <summary>Kept for the life of the process: closing it is what kills the job.</summary>
        private static IntPtr _job;

        private static void TryJoinKillOnCloseJob()
        {
            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

                var info = new JobObjectExtendedLimitInformationStruct();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>()))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                using (var self = System.Diagnostics.Process.GetCurrentProcess())
                {
                    if (!AssignProcessToJobObject(job, self.Handle))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }

                _job = job;
            }
            catch (Exception e)
            {
                //Not fatal: Ctrl+C, stdin close and a normal exit still kill the children. Said because a hard kill
                //of the CLI would then leave them running.
                Console.Error.WriteLine("WARNING: could not put the campaign in a kill-on-close job object ("
                                        + e.Message + "); if this process is killed outright its children keep running.");
            }
        }
    }
}
