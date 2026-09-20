using System;
using System.Runtime.InteropServices;

namespace TimberNet.Perf
{
    /// <summary>
    /// How much processor time the game thread and the whole process used, from Windows. Set beside the wall clock it says whether a slow
    /// frame was the game thread working or waiting (for the graphics card, for vertical sync, for another thread, or because the
    /// system did not run it). Windows only; anywhere else, or if the calls fail, it reports that it is unavailable and stays out of the way.
    /// The thread's time is charged in scheduler quanta, so one frame's figure is coarse and only sums over many frames are exact.
    /// </summary>
    public static class PerfCpu
    {
        public static bool Available { get; private set; }

        static IntPtr processHandle;

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        /// <summary>Call on the game thread. False if this system cannot say.</summary>
        public static bool Init()
        {
            Available = false;
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
                processHandle = GetCurrentProcess();
                Available = Sample(out _, out _, out _);
            }
            catch (Exception)
            {
                Available = false;
            }
            return Available;
        }

        /// <summary>
        /// The game thread's and the process's processor time so far, in 100 nanosecond units, and the game thread's cycle count.
        /// Must be called from the thread being measured.
        /// </summary>
        public static bool Sample(out long threadCpu, out ulong threadCycles, out long processCpu)
        {
            threadCpu = 0; threadCycles = 0; processCpu = 0;
            try
            {
                IntPtr thread = GetCurrentThread();
                if (!GetThreadTimes(thread, out _, out _, out long threadKernel, out long threadUser)) return false;
                if (!QueryThreadCycleTime(thread, out threadCycles)) return false;
                if (!GetProcessTimes(processHandle, out _, out _, out long processKernel, out long processUser)) return false;
                threadCpu = threadKernel + threadUser;
                processCpu = processKernel + processUser;
                return true;
            }
            catch (Exception)
            {
                Available = false;
                return false;
            }
        }
    }
}
