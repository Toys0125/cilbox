# Cilbox thread CPU clock providers

The execution ledger uses a bound current-thread CPU reader to count interpreter
work and synchronous host/native CPU work while excluding blocked and
descheduled intervals. Each reader can also be sampled from another thread for
frame-boundary snapshots. Its `Name` and `IsCpuTime` identify the active mode.

## Platform support

| Platform | Backend | Precision / limitations |
| --- | --- | --- |
| Linux x64 / ARM64, Mono / IL2CPP / CoreCLR | `pthread_getcpuclockid(pthread_self())` plus `clock_gettime` | User + kernel CPU for the selected thread; requires a 64-bit Linux ABI and advertised resolution at most 0.1 ms. No custom native plugin required. |
| Android ARM64 | Same POSIX APIs from `libc` | Implemented but needs a device/player validation run; 32-bit ABIs deliberately fall back. |
| Native Windows x64, Mono / IL2CPP / CoreCLR | `QueryThreadCycleTime` divided by a qualified invariant-TSC rate | Estimated CPU time, supported only when native-x64, invariant-TSC, rate stability, wait exclusion, and busy-scale checks pass. The bundled DLL supplies startup-only calibration; validate the selected backend in the actual player. |
| Windows ARM64, x64 emulation on ARM64, Windows x86 | Stopwatch fallback | No qualified precise thread-time backend; cannot preserve CPU-only semantics. |
| macOS, iOS, WebGL, other/unknown ABIs | Stopwatch fallback | Not implemented by this change. |

Fallback retains the configured budget values but measures wall time. It emits a
one-time diagnostic in Unity and exposes `IsCpuTime == false`; waits can count
in this mode. A failed read returns false rather than a zero time. The ledger
must handle that failure explicitly without mixing clock epochs.

The clock interface is internal and injectable in the standalone regression
tests. `Dispose` releases the duplicated Windows thread handle. Linux clock IDs
do not own resources and must not outlive an active owning thread/context.

## Windows qualification

Microsoft explicitly warns that `QueryThreadCycleTime` cycles are not generally
convertible into elapsed time:

- https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-querythreadcycletime
- Chromium's constrained Windows implementation:
  https://chromium.googlesource.com/chromium/src/+/HEAD/base/time/time_win.cc

This helper therefore does not divide by CPU marketing GHz or
`Stopwatch.Frequency`. It:

1. Rejects non-native-x64 Windows, including x64 emulation on ARM64.
2. Requires the CPUID invariant-TSC bit.
3. Takes three independent paired TSC/QPC samples around short sleeps.
4. Rejects descheduling gaps within an individual paired sample.
5. Requires the three estimated rates to agree within 0.5%.
6. Checks that a sleep contributes little thread CPU and that at least one
   largely scheduled busy sample agrees with the scale.

These checks qualify a platform-dependent estimate; they cannot guarantee all
future virtualization, firmware, CPU-migration, or power-state behavior. The
stopwatch deadline should be conservative (for example, 1% earlier than the
nominal remaining CPU time); the final CPU comparison still uses the original
budget threshold. Do not label the converted units as guaranteed nanosecond
accuracy.

### Bounded startup retry policy

One attempt normally includes about 80 ms of requested sleeps and up to 6 ms
of busy checks. Query failure (3), unstable calibration (4), and thread-cycle
qualification failure (5) get **at most three total attempts**, with a 20 ms
backoff between attempts. Unsupported architecture (1), missing invariant TSC
(2), and unknown status codes stop immediately. The conservative acceptance
checks and tolerances above are unchanged.

Every attempt starts with a zero candidate frequency. A failed candidate is
discarded; success without a frequency is treated as query failure. The native
`InitOnce` callback commits the final success or fallback after the bounded
policy completes. It never returns `FALSE` for an ordinary qualification failure,
so later clock opens cannot restart qualification indefinitely.

At the maximum attempt count, initialization requests approximately 280 ms of
sleeps plus at most 18 ms of busy checks. Scheduling can make actual wall duration
longer. Unity prewarms before scene scripts run, and the managed admission barrier
prepares the clock before the first Cilbox activation on any thread. Preparation
publishes a terminal result for both success and fallback; subsequent entries
and the 64-instruction checkpoints never recalibrate.

The helper does not adjust thread priority, affinity, process-wide timer
resolution, or power policy. Persistent qualification failure still selects the
explicitly diagnosed Stopwatch fallback for the process. Automatic retries after
execution has started are deliberately not part of this policy.

### Native API

| Export | Behavior |
| --- | --- |
| `cilbox_cpu_clock_frequency(uint64_t*)` | Existing initializer ABI, retained for guarded startup preparation and standalone native probes. Serializes through `InitOnce` and returns the final result after the bounded attempts. |
| `cilbox_cpu_clock_cached_frequency(uint64_t*)` | Nonblocking lookup only. Never invokes or waits for `InitOnce`, sleeps, or retries. Returns status 6 and zero frequency until the terminal result is published. Managed reader opening uses only this export. |

The initializer's caller must exclude all active Cilbox execution while
preparing the clock. The managed preparation barrier enforces this by completing
before any activation is admitted. The cached export publishes no intermediate
candidate and returns a frequency only for successful qualification.

| Result | Meaning / retry policy |
| --- | --- |
| 0 | Qualified; output contains the estimated TSC ticks per second. |
| 1 | Unsupported native architecture; no retry. |
| 2 | Invariant TSC not reported; no retry. |
| 3 | Native query/calibration failed; retry within the startup bound. |
| 4 | Frequency samples unstable or implausible; retry within the startup bound. |
| 5 | Thread-cycle wait/busy qualification failed; retry within the startup bound. |
| 6 | Cached lookup is not prepared; lookup never initializes or retries. |

`CilboxCpuClockRetry.h` contains the exact production attempt policy, including
fresh candidates, success-frequency validation, status classification, attempt
bound, and backoff placement. Portable fault-injection tests include this header
and substitute attempt/wait callbacks; they do not duplicate the algorithm.
Those tests do not by themselves prove Win32 `InitOnce` behavior or clock accuracy.

## Reproducible bundled DLL

Run from the repository root with the MinGW x64 cross toolchain:

```sh
x86_64-w64-mingw32-gcc -O2 -std=c11 -Wall -Wextra -Werror -ffreestanding -fno-builtin -fno-stack-protector -fno-ident -shared -nostdlib -Wl,--entry=DllMain -Wl,--no-insert-timestamp Packages/com.cnlohr.cilbox/Native~/CilboxCpuClockWin64.c -o Packages/com.cnlohr.cilbox/Plugins/x86_64/CilboxCpuClockWin64.dll -lkernel32
```

Toolchain used for the checked-in binary:

- Ubuntu package `gcc-mingw-w64-x86-64-posix` `13.2.0-6ubuntu1+26.1`.
- Compiler identifies as `x86_64-w64-mingw32-gcc (GCC) 13-posix`.
- GNU ld `2.41.90.20240122`.
- PE32+ x86-64 DLL, 11,693 bytes; timestamp zero.
- Imports only `KERNEL32.dll`; no MSVC runtime or MinGW runtime DLL dependency.

SHA-256:

- `CilboxCpuClockWin64.c`:
  `8a8c6995ba7ccc6b19a4652b9d5c1f11e85762dbea90a856b9046f8c6aa2d835`
- `CilboxCpuClockRetry.h`:
  `01c64797e5d48bc907138689a521e794cf8575621ce1168b6c6478a7d759076c`
- `CilboxCpuClockWin64.dll`:
  `814909320f9c8c6620c1a7c6bf8372e619d47a37c187f82dc6338d7584a8fdbb`

The plugin importer enables this binary only for Windows x64 Editor and
Standalone. Native sources live in `Native~` so Unity does not compile them into
IL2CPP players a second time. Mono receives the prebuilt DLL; IL2CPP calls the
same C exports through ordinary P/Invoke.

## Validation limits

The available Linux ARM64 host can compile and inspect the Windows DLL but
cannot run it. A Windows runtime/Unity player test must verify qualification,
DLL loading, waits, contention, synchronous native CPU work, and accounting in
the actual supported Unity versions. The standalone .NET tests are not a
substitute for Mono, IL2CPP, or Unity CoreCLR player validation.
