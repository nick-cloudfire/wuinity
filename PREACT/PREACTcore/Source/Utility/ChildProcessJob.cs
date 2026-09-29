using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PREACT.Utility
{
    /// <summary>
    /// Ties every external process this one starts to its own lifetime, so nothing outlives the program that
    /// launched it.
    /// </summary>
    /// <remarks>
    /// The problem is that killing a process on Windows does not kill what it started. The campaign runs three
    /// deep — Unity starts <c>PREACTcli</c>, which starts <c>elmfire.exe</c> and <c>WindNinja_cli.exe</c> —
    /// and cancelling from the GUI only ever killed the middle one. Measured: a driver launched at lunchtime
    /// was still alive the following morning with thirteen <c>elmfire.exe</c> under it, one of them holding
    /// twelve CPU-hours, and it was still spawning new children. They keep whole cores busy, they hold the
    /// binaries open so ELMFIRE cannot be rebuilt, and nothing in the interface shows they are there.
    ///
    /// A Windows job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> is the mechanism the OS provides
    /// for exactly this: every process assigned to the job is terminated when the last handle to it closes.
    /// Since the handle is held by this process and never closed explicitly, that moment is precisely when
    /// this process ends — cleanly, killed, or crashed. It needs no cooperation from the child, which matters
    /// because <c>elmfire.exe</c> is an MPI binary that does not shut down on request.
    ///
    /// Assign at both levels and the whole tree collapses: Unity's job takes <c>PREACTcli</c>, and
    /// <c>PREACTcli</c>'s job takes ELMFIRE and WindNinja. Jobs have nested since Windows 8, so a process
    /// already inside one can join another.
    ///
    /// The handle is deliberately a raw <see cref="IntPtr"/> rather than a SafeHandle: a SafeHandle carries a
    /// finalizer, and under the Unity editor a domain reload can finalize managed state while the process
    /// lives on, which would close the job and kill a running campaign on recompile. Leaving the OS to close
    /// it at process exit is both simpler and the behaviour wanted.
    ///
    /// Everything here is best-effort and silent. Failing to track a child is a leaked process; throwing from
    /// here would be a failed simulation.
    /// </remarks>
    public static class ChildProcessJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private static readonly object Sync = new object();
        private static IntPtr _job = IntPtr.Zero;
        private static bool _tried;

        /// <summary>
        /// Puts <paramref name="process"/> under this program's lifetime. Safe to call on any platform and
        /// with a process that has already exited.
        /// </summary>
        public static void Track(Process process)
        {
            if (process == null) return;

            //Nothing to do off Windows: on Unix the shell and the process group already handle this, and
            //there is no job object to assign to.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

            try
            {
                IntPtr job = GetOrCreateJob();
                if (job == IntPtr.Zero) return;

                //Handle rather than Id: AssignProcessToJobObject wants the handle, and reading it after the
                //process has exited throws rather than returning zero.
                IntPtr handle = process.Handle;
                if (handle == IntPtr.Zero) return;

                AssignProcessToJobObject(job, handle);
            }
            catch
            {
                //An untracked child is worse than a tracked one and better than a crash.
            }
        }

        private static IntPtr GetOrCreateJob()
        {
            lock (Sync)
            {
                if (_tried) return _job;
                _tried = true;

                try
                {
                    //Unnamed: this job belongs to this process and nothing else should join it.
                    IntPtr job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return IntPtr.Zero;

                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

                    int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        Marshal.StructureToPtr(info, buffer, false);
                        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                        {
                            //A job without the kill-on-close limit would silently do nothing, so it is not
                            //kept: better to leave children untracked than to believe they are tracked.
                            CloseHandle(job);
                            return IntPtr.Zero;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    _job = job;
                }
                catch
                {
                    _job = IntPtr.Zero;
                }

                return _job;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
