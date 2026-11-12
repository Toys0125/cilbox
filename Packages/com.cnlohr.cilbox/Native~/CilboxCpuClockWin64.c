/*
 * Cilbox Windows x64 thread-cycle frequency qualification.
 *
 * QueryThreadCycleTime is not a portable CPU-time clock. Microsoft explicitly
 * warns against generally converting its cycles to elapsed time. This helper
 * supports only native x64 Windows with an invariant TSC and successful local
 * qualification, following the constrained approach used by Chromium ThreadTicks.
 *
 * No allocations, CRT, drivers, elevated privileges, or process-wide settings.
 * One attempt normally includes about 80 ms of sleeps and up to 6 ms of busy
 * checks. Retryable failures get at most three total attempts, with 20 ms
 * backoff between attempts. Run preparation before any interpreter activation.
 */
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#include <windows.h>
#include <intrin.h>
#include <stdint.h>
#include "CilboxCpuClockRetry.h"

#if !defined(_M_X64) && !defined(__x86_64__)
#error Build this helper for Windows x64 only.
#endif

static INIT_ONCE initialize_once = INIT_ONCE_STATIC_INIT;
static uint64_t qualified_frequency;
static int qualification_status = CILBOX_CLOCK_NOT_PREPARED;
static volatile LONG initialization_finished;

static uint64_t read_tsc(void)
{
    _mm_lfence();
    uint64_t ticks = __rdtsc();
    _mm_lfence();
    return ticks;
}

/* Bracket the TSC reading to reject a descheduling gap during the sample.
 * The gap between separate sample pairs can include sleeps/preemption safely
 * because both invariant TSC and QPC continue progressing during that gap. */
static int paired_sample(LONGLONG qpc_frequency, LONGLONG* qpc, uint64_t* tsc)
{
    for (int attempt = 0; attempt < 8; ++attempt) {
        LARGE_INTEGER before, after;
        if (!QueryPerformanceCounter(&before)) return 0;
        uint64_t value = read_tsc();
        if (!QueryPerformanceCounter(&after)) return 0;
        LONGLONG width = after.QuadPart - before.QuadPart;
        if (width >= 0 && width <= qpc_frequency / 100000) { /* 10 us */
            *qpc = before.QuadPart + width / 2;
            *tsc = value;
            return 1;
        }
    }
    return 0;
}

static int native_x64_windows(void)
{
    typedef BOOL (WINAPI *IsWow64Process2Function)(HANDLE, USHORT*, USHORT*);
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    IsWow64Process2Function query = kernel
        ? (IsWow64Process2Function)(void*)GetProcAddress(kernel, "IsWow64Process2") : 0;
    if (query) {
        USHORT process_machine, native_machine;
        if (!query(GetCurrentProcess(), &process_machine, &native_machine)) return -1;
        return native_machine == IMAGE_FILE_MACHINE_AMD64
            && process_machine == IMAGE_FILE_MACHINE_UNKNOWN;
    }
    SYSTEM_INFO info;
    GetNativeSystemInfo(&info);
    return info.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_AMD64;
}

static int qualify_thread_cycles(uint64_t frequency, LONGLONG qpc_frequency)
{
    HANDLE current = GetCurrentThread();
    ULONG64 cycle0, cycle1;
    LARGE_INTEGER wall0, wall1;

    /* A blocking wait must not turn into a CPU-sized interval. */
    if (!QueryThreadCycleTime(current, &cycle0)
        || !QueryPerformanceCounter(&wall0)) return 0;
    Sleep(20);
    if (!QueryThreadCycleTime(current, &cycle1)
        || !QueryPerformanceCounter(&wall1)
        || cycle1 < cycle0 || wall1.QuadPart <= wall0.QuadPart) return 0;
    double cpu = (double)(cycle1 - cycle0) / (double)frequency;
    double wall = (double)(wall1.QuadPart - wall0.QuadPart) / (double)qpc_frequency;
    if (cpu > 0.005 || cpu > wall * 0.25) return 0;

    /* At least one largely scheduled sample must agree with the calibrated
     * scale. Retry a few times because the OS can preempt this thread.
     * This is a qualification check, not a universal accuracy guarantee. */
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (!QueryThreadCycleTime(current, &cycle0)
            || !QueryPerformanceCounter(&wall0)) return 0;
        do {
            if (!QueryPerformanceCounter(&wall1)) return 0;
            YieldProcessor();
        } while (wall1.QuadPart - wall0.QuadPart < qpc_frequency / 500);
        if (!QueryThreadCycleTime(current, &cycle1)
            || !QueryPerformanceCounter(&wall1)
            || cycle1 < cycle0 || wall1.QuadPart <= wall0.QuadPart) return 0;
        cpu = (double)(cycle1 - cycle0) / (double)frequency;
        wall = (double)(wall1.QuadPart - wall0.QuadPart) / (double)qpc_frequency;
        double ratio = cpu / wall;
        if (ratio >= 0.8 && ratio <= 1.005) return 1;
    }
    return 0;
}

static int qualify_once(void* context, uint64_t* out_frequency)
{
    (void)context;

    int architecture = native_x64_windows();
    if (architecture < 0) return CILBOX_CLOCK_QUERY_FAILED;
    if (!architecture) return CILBOX_CLOCK_UNSUPPORTED_ARCHITECTURE;

    int registers[4];
    __cpuid(registers, (int)0x80000000u);
    if ((uint32_t)registers[0] < 0x80000007u)
        return CILBOX_CLOCK_NO_INVARIANT_TSC;
    __cpuid(registers, (int)0x80000007u);
    if (((uint32_t)registers[3] & (1u << 8)) == 0)
        return CILBOX_CLOCK_NO_INVARIANT_TSC;

    LARGE_INTEGER qpc_frequency;
    if (!QueryPerformanceFrequency(&qpc_frequency) || qpc_frequency.QuadPart <= 0)
        return CILBOX_CLOCK_QUERY_FAILED;

    double samples[3];
    for (int i = 0; i < 3; ++i) {
        LONGLONG q0, q1;
        uint64_t t0, t1;
        if (!paired_sample(qpc_frequency.QuadPart, &q0, &t0))
            return CILBOX_CLOCK_QUERY_FAILED;
        Sleep(20);
        if (!paired_sample(qpc_frequency.QuadPart, &q1, &t1)
            || q1 <= q0 || t1 <= t0)
            return CILBOX_CLOCK_QUERY_FAILED;
        samples[i] = (double)(t1 - t0) * (double)qpc_frequency.QuadPart
            / (double)(q1 - q0);
    }

    /* Median of the three rates, with every sample within 0.5%. */
    for (int i = 0; i < 2; ++i)
        for (int j = i + 1; j < 3; ++j)
            if (samples[i] > samples[j]) {
                double temporary = samples[i];
                samples[i] = samples[j];
                samples[j] = temporary;
            }
    double median = samples[1];
    if (median < 1000000.0 || median > 20000000000.0
        || samples[0] < median * 0.995 || samples[2] > median * 1.005)
        return CILBOX_CLOCK_UNSTABLE_FREQUENCY;

    uint64_t frequency = (uint64_t)(median + 0.5);
    if (!qualify_thread_cycles(frequency, qpc_frequency.QuadPart))
        return CILBOX_CLOCK_THREAD_CYCLES_FAILED_QUALIFICATION;
    *out_frequency = frequency;
    return CILBOX_CLOCK_OK;
}

static void wait_before_retry(void* context, unsigned int delay_ms)
{
    (void)context;
    Sleep((DWORD)delay_ms);
}

static BOOL CALLBACK initialize_clock(PINIT_ONCE once, PVOID parameter, PVOID* context)
{
    (void)once;
    (void)parameter;
    (void)context;

    qualification_status = cilbox_clock_qualify_bounded(
        qualify_once, wait_before_retry, 0, &qualified_frequency);
    /* TRUE commits success OR terminal failure. FALSE would let arbitrary later
     * clock opens trigger more qualification work inside active execution. */
    return TRUE;
}

/* Initializer ABI retained for startup preparation and standalone native probes.
 * Callers must exclude all active Cilbox execution while preparing the clock. */
__declspec(dllexport) int __cdecl cilbox_cpu_clock_frequency(uint64_t* ticks_per_second)
{
    if (!ticks_per_second) return CILBOX_CLOCK_QUERY_FAILED;
    *ticks_per_second = 0;
    if (!InitOnceExecuteOnce(&initialize_once, initialize_clock, 0, 0))
        return CILBOX_CLOCK_QUERY_FAILED;

    /* Publish only after the initializer callback and its bounded policy finish.
     * Interlocked operations provide the publication fence for the cached API. */
    InterlockedExchange(&initialization_finished, 1);
    *ticks_per_second = qualified_frequency;
    return qualification_status;
}

/* Nonblocking lookup: never runs, waits for, or retries initialization.
 * During preparation it returns NOT_PREPARED and a zero frequency. */
__declspec(dllexport) int __cdecl cilbox_cpu_clock_cached_frequency(uint64_t* ticks_per_second)
{
    if (!ticks_per_second) return CILBOX_CLOCK_QUERY_FAILED;
    *ticks_per_second = 0;
    if (InterlockedCompareExchange(&initialization_finished, 0, 0) == 0)
        return CILBOX_CLOCK_NOT_PREPARED;
    *ticks_per_second = qualified_frequency;
    return qualification_status;
}

/* The MinGW build links without the CRT; there is no loader-time work. */
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    (void)reason;
    (void)reserved;
    return TRUE;
}
