using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cilbox
{
    // A reader is bound to the OS thread that opened it. Reading it from another
    // thread is supported so a frame boundary can settle active worker scopes.
    internal interface ICilboxThreadCpuClock : IDisposable
    {
        string Name { get; }
        bool IsCpuTime { get; }
        bool TryReadNanoseconds(out long nanoseconds);
    }

    internal static class CilboxThreadCpuClock
    {
        private static int fallbackWarningReported;
        private const long NanosecondsPerSecond = 1000000000;
        private const long MaximumResolutionNanoseconds = 100000; // 0.1 ms.

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string PthreadLibrary = "libc";
#else
        private const string PthreadLibrary = "libpthread.so.0";
#endif

#if UNITY_5_3_OR_NEWER
        // Windows frequency qualification includes short sleeps. Do that before
        // script dispatch rather than inside the first interpreter budget.
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void PrewarmMainThreadClock()
        {
            using (ICilboxThreadCpuClock clock = OpenCurrentThread()) { }
        }
#endif

        internal static ICilboxThreadCpuClock OpenCurrentThread()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return OpenWindowsClock();

#if UNITY_ANDROID && !UNITY_EDITOR
                bool isLinux = true;
#else
                bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
#endif
                if (isLinux && IntPtr.Size == 8)
                    return OpenLinuxClock();

                return OpenStopwatchFallback("no qualified thread CPU clock for this platform/ABI");
            }
            catch (DllNotFoundException exception)
            {
                return OpenStopwatchFallback(exception.Message);
            }
            catch (EntryPointNotFoundException exception)
            {
                return OpenStopwatchFallback(exception.Message);
            }
            catch (BadImageFormatException exception)
            {
                return OpenStopwatchFallback(exception.Message);
            }
            catch (PlatformNotSupportedException exception)
            {
                return OpenStopwatchFallback(exception.Message);
            }
        }

        internal static ICilboxThreadCpuClock OpenStopwatchFallback(string reason)
        {
            if (Interlocked.Exchange(ref fallbackWarningReported, 1) == 0)
            {
                string message = "Cilbox thread CPU budgeting is unavailable: " + reason
                    + ". Using Stopwatch wall-time budgets; blocked/descheduled time may count.";
#if UNITY_5_3_OR_NEWER
                UnityEngine.Debug.LogWarning(message);
#else
                Trace.TraceWarning(message);
#endif
            }
            return new StopwatchClock(reason);
        }

        private static ICilboxThreadCpuClock OpenLinuxClock()
        {
            int clockId;
            int error = pthread_getcpuclockid(pthread_self(), out clockId);
            if (error != 0)
                return OpenStopwatchFallback("pthread_getcpuclockid failed (" + error + ")");

            Timespec resolution;
            if (clock_getres(clockId, out resolution) != 0
                || resolution.Seconds != 0 || resolution.Nanoseconds < 0
                || resolution.Nanoseconds > MaximumResolutionNanoseconds)
                return OpenStopwatchFallback("thread CPU clock resolution is unavailable or above 0.1 ms");

            var clock = new LinuxThreadClock(clockId);
            long initial;
            if (!clock.TryReadNanoseconds(out initial))
                return OpenStopwatchFallback("clock_gettime failed for the current thread");
            return clock;
        }

        private static ICilboxThreadCpuClock OpenWindowsClock()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64
                || RuntimeInformation.OSArchitecture != Architecture.X64)
                return OpenStopwatchFallback("the qualified Windows CPU clock requires native x64 Windows");

            ulong frequency;
            int status = cilbox_cpu_clock_frequency(out frequency);
            if (status != 0 || frequency == 0)
                return OpenStopwatchFallback("Windows invariant-TSC clock qualification failed (" + status + ")");

            IntPtr process = GetCurrentProcess();
            IntPtr handle;
            // A pseudo handle means the thread making the query, so retain a
            // real handle for correct frame snapshots on a different thread.
            if (!DuplicateHandle(process, GetCurrentThread(), process, out handle,
                0, false, 2 /* DUPLICATE_SAME_ACCESS */))
                return OpenStopwatchFallback("DuplicateHandle failed (" + Marshal.GetLastWin32Error() + ")");

            var clock = new WindowsThreadClock(handle, frequency);
            long initial;
            if (!clock.TryReadNanoseconds(out initial))
            {
                clock.Dispose();
                return OpenStopwatchFallback("QueryThreadCycleTime failed for the current thread");
            }
            return clock;
        }

        private sealed class StopwatchClock : ICilboxThreadCpuClock
        {
            private readonly string reason;
            internal StopwatchClock(string reason) { this.reason = reason; }
            public string Name { get { return "Stopwatch wall time (fallback: " + reason + ")"; } }
            public bool IsCpuTime { get { return false; } }
            public bool TryReadNanoseconds(out long nanoseconds)
            {
                return TryScaleToNanoseconds((ulong)Stopwatch.GetTimestamp(),
                    (ulong)Stopwatch.Frequency, out nanoseconds);
            }
            public void Dispose() { }
        }

        private sealed class LinuxThreadClock : ICilboxThreadCpuClock
        {
            private readonly int clockId;
            internal LinuxThreadClock(int clockId) { this.clockId = clockId; }
            public string Name { get { return "Linux thread CPU time"; } }
            public bool IsCpuTime { get { return true; } }
            public bool TryReadNanoseconds(out long nanoseconds)
            {
                Timespec value;
                nanoseconds = 0;
                if (clock_gettime(clockId, out value) != 0 || value.Seconds < 0
                    || value.Nanoseconds < 0 || value.Nanoseconds >= NanosecondsPerSecond
                    || value.Seconds > (long.MaxValue - value.Nanoseconds) / NanosecondsPerSecond)
                    return false;
                nanoseconds = value.Seconds * NanosecondsPerSecond + value.Nanoseconds;
                return true;
            }
            public void Dispose() { } // clock IDs own no handles.
        }

        private sealed class WindowsThreadClock : ICilboxThreadCpuClock
        {
            private readonly object handleLock = new object();
            private readonly ulong frequency;
            private IntPtr handle;

            internal WindowsThreadClock(IntPtr handle, ulong frequency)
            {
                this.handle = handle;
                this.frequency = frequency;
            }

            public string Name { get { return "Windows calibrated invariant-TSC thread CPU time"; } }
            public bool IsCpuTime { get { return true; } }

            public bool TryReadNanoseconds(out long nanoseconds)
            {
                // IntPtr P/Invoke works in Mono and IL2CPP without depending on
                // runtime-specific SafeHandle marshaling. Serialize disposal.
                lock (handleLock)
                {
                    nanoseconds = 0;
                    ulong cycles;
                    return handle != IntPtr.Zero && QueryThreadCycleTime(handle, out cycles)
                        && TryScaleToNanoseconds(cycles, frequency, out nanoseconds);
                }
            }

            public void Dispose()
            {
                ReleaseHandle();
                GC.SuppressFinalize(this);
            }

            ~WindowsThreadClock() { ReleaseHandle(); }

            private void ReleaseHandle()
            {
                lock (handleLock)
                {
                    if (handle == IntPtr.Zero) return;
                    CloseHandle(handle);
                    handle = IntPtr.Zero;
                }
            }
        }

        private static bool TryScaleToNanoseconds(ulong value, ulong frequency, out long nanoseconds)
        {
            nanoseconds = 0;
            if (frequency == 0) return false;
            ulong seconds = value / frequency;
            if (seconds > (ulong)(long.MaxValue / NanosecondsPerSecond)) return false;
            // Divide first to avoid overflowing cycles * 1e9 and to preserve
            // precision after a thread has accumulated a large total.
            long fraction = (long)((value % frequency) * (1e9 / frequency));
            if (fraction < 0 || fraction >= NanosecondsPerSecond) return false;
            long whole = (long)seconds * NanosecondsPerSecond;
            if (whole > long.MaxValue - fraction) return false;
            nanoseconds = whole + fraction;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            internal long Seconds;
            internal long Nanoseconds;
        }

        [DllImport(PthreadLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr pthread_self();

        [DllImport(PthreadLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pthread_getcpuclockid(UIntPtr thread, out int clockId);

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clock_gettime(int clockId, out Timespec value);

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clock_getres(int clockId, out Timespec value);

        [DllImport("CilboxCpuClockWin64", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int cilbox_cpu_clock_frequency(out ulong ticksPerSecond);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
            IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
