# Cilbox CPU-budget regressions

This executable runs the actual Cilbox interpreter with the repository's existing
Unity API stubs. It tests the production clock/accounting code compiled directly
from `Packages/com.cnlohr.cilbox`. A .NET console run uses CoreCLR, but **it is not
a Unity CoreCLR player test**, nor Mono or IL2CPP player validation.

## Run the regressions

The default target is .NET 8, with no external NuGet packages:

```sh
dotnet run --project TestCilboxCpuBudget/TestCilboxCpuBudget.csproj -c Release
```

The managed suite currently contains 31 cases. The process emits JSON containing
the runtime, architecture, selected CPU clock, named results, and failure details.
Exit code 0 means every test passed; 1 means a regression failed; 2 means
initialization or platform support failed.

On Linux, a C compiler is required. The project builds a tiny shared library into
its output directory to verify that synchronous native CPU work counts. On
Windows, the project copies the packaged x64 CPU-clock DLL into its output
directory and uses a synchronous managed host workload. Both variants call the
same interpreted host-dispatch path. A missing or unsupported precise clock is
reported as a failed validation prerequisite; the harness does not call a wall
fallback a successful CPU-budget test.

For .NET 10, install its SDK/runtime, explicitly restore the alternate target,
then build and run that target:

```sh
dotnet restore TestCilboxCpuBudget/TestCilboxCpuBudget.csproj -p:TargetFramework=net10.0 --force
dotnet build TestCilboxCpuBudget/TestCilboxCpuBudget.csproj -c Release -p:TargetFramework=net10.0 --no-restore -o TestCilboxCpuBudget/artifacts/net10
dotnet TestCilboxCpuBudget/artifacts/net10/TestCilboxCpuBudget.dll
```

An explicit restore avoids stale assets when switching between .NET 8 and .NET 10.
The project remains restorable with an SDK 8-only environment by default.

## What the tests cover

Actual interpreter regressions warm the interpreter, reflection dispatch, clock
provider, host workload, and relevant exception paths before using a 5 ms limit.

- A 50 ms wait remains within the CPU allowance.
- An infinite interpreted loop still times out.
- Synchronous CPU work is checked even when the next instruction is `ret`.
- Short calls share a frame budget, and an exhausted/disabled owner cannot run
  another host call.
- CPU consumed before a wait remains charged afterward.
- The real `Update` callback reports CPU from the completed frame and restores
  the new frame's allowance.
- Ordinary exceptions unwind accounting while retaining their identity.
- Same-owner callback recursion counts CPU once; distinct owners receive
  inclusive charges.
- Nested child or parent exhaustion disables the correct owner.
- An interpreted catch clause catches an ordinary host exception, but cannot
  swallow a CPU-budget fault propagated through a nested host callback.
- Virtual dispatch into a receiver owned by another box gets its own inclusive
  scope. A caught ordinary exception leaves that receiver usable; a budget
  timeout disables only the responsible owner or its receiver proxy.

Deterministic tests inject bound per-thread CPU readers and a separate wall
clock into the accounting helper. They verify exact A/A and A/B/A totals,
wait-gate rebuilding, active frame splits, foreign-thread snapshots, aggregate
concurrent consumption, clock failures, admission of zero/negative budgets,
unwind behavior, and preservation of an already detected overrun across a frame
reset and budget change. A mixed fallback/CPU clock history remains clearly
identified in diagnostics. The injected factories are restored after each test,
and the tests verify that readers and active scopes were released.

Preparation-barrier tests use the real `Enter` path with an isolated
`CilboxClockPreparation`. They verify that concurrent entries wait before any
reader or accounting scope exists, preparation CPU precedes the first budget,
and nested/later entries never repeat a completed preparation. A terminal
fallback cannot be retried or change an active reader. Unexpected exceptions
are cached and preserve their identity, and recursive entry during preparation
is rejected without leaking a scope.

These tests do not attribute CPU performed on unrelated worker or GC threads.
They also do not establish Unity player compatibility, DLL deployment in a
player, hardware clock behavior on untested machines, or native-call preemption.

## Test the bounded Windows qualification policy without Windows hardware

The portable C harness includes the exact retry-policy header used by the
Windows DLL. From the repository root:

```sh
cc -std=c11 -O2 -Wall -Wextra -Werror TestCilboxCpuBudget/native_qualification_policy_tests.c -o /tmp/cilbox-clock-policy-tests
/tmp/cilbox-clock-policy-tests
```

The 14 cases inject complete qualification outcomes. They cover a transient
status 5 followed by success, statuses 3 and 4, permanent statuses 1 and 2, and
exhaustion after exactly three total attempts. They also verify at most two
20 ms retry waits, no wait after termination, no publication of failed
candidates, a fresh candidate for every attempt, unknown-status rejection, and
rejection of success without a positive frequency. The waits are injected
callbacks; the policy tests do not sleep.

This tests the shared retry policy without weakening or bypassing the real
qualification checks in the DLL. It does not exercise Windows `InitOnce`,
physical TSC/QPC behavior, DLL loading, or actual Windows clock precision.
The managed barrier tests separately cover one-time serialization and admission
before interpreter execution. The revised DLL still requires a Windows run.

## Measure interpreter overhead

The benchmark is separate from the correctness run:

```sh
DOTNET_TieredCompilation=0 dotnet run --project TestCilboxCpuBudget/TestCilboxCpuBudget.csproj -c Release -- --benchmark-only
```

For Windows PowerShell, set `$env:DOTNET_TieredCompilation = "0"` before running
the equivalent `dotnet` command.

There are 3 warmup and 9 measured batches per workload. The two long workloads
use 8 calls of 262,145 interpreted instructions per batch: one uses NOPs, and the
other repeats `ldc.i4.1; ldc.i4.2; add; pop`. A third workload uses 10,000 separate
single-`ret` callbacks per batch and records CPU and wall nanoseconds per call,
including entry/exit clock reads, clock acquisition, and accounting. This exposes
boundary costs that the long interpreter loops amortize away. CPU duration and
elapsed wall duration are recorded independently. Two additional loops perform
100,000 reads per batch of `Stopwatch.GetTimestamp()` or the CPU measurement
wrapper. They report full loop/call costs (including CPU-wrapper dispatch and a
checksum) without subtraction. The implementation's wrapper uses the exact
bound production reader; the Linux baseline wrapper uses the current thread
CPU clock only for measurement.

A generous 2-second allowance keeps the interpreter benchmarks from
timing out and normally allows the wall gate to skip checkpoint CPU reads.
Therefore the result is the favorable cost of the guard, not a universal
overhead claim for near-budget, waiting, native-heavy, or concurrent workloads.

For a fair baseline, compile the same harness against an unchanged checkout's
package source. Baseline mode excludes tests that require the new accounting
API and supports `--benchmark-only` on 64-bit Linux:

```sh
dotnet restore TestCilboxCpuBudget/TestCilboxCpuBudget.csproj -p:Baseline=true -p:CilboxSourceRoot=/path/to/baseline/Packages/com.cnlohr.cilbox --force
dotnet build TestCilboxCpuBudget/TestCilboxCpuBudget.csproj -c Release -p:Baseline=true -p:CilboxSourceRoot=/path/to/baseline/Packages/com.cnlohr.cilbox --no-restore -o TestCilboxCpuBudget/artifacts/baseline
DOTNET_TieredCompilation=0 dotnet TestCilboxCpuBudget/artifacts/baseline/TestCilboxCpuBudget.dll --benchmark-only
```

Use matching target frameworks, runtime versions, build options, hardware, and
environment variables for both runs. Record the source revision for each. A
different .NET JIT can change both interpreter throughput and the relative
overhead; do not transfer a .NET 8 percentage to .NET 10 or to a Unity player.

## Recorded Linux ARM64 results

These performance samples predate the bounded-startup-retry follow-up. Their
source hashes and original 26-case correctness results are preserved below;
they are not a fresh benchmark of the preparation barrier. The current managed
suite adds five preparation cases and passed 31/31 on .NET 8 and .NET 10.

The included [net8 results](results/linux-arm64-net8.json) and
[net10 results](results/linux-arm64-net10.json) preserve all 9 measured samples,
the matching regression results, and SHA-256 hashes of the production and test
sources. Both correctness runs passed **26/26** cases with no build warnings or
errors. The baseline is upstream commit
`d7f0c8d934dc2721362c4c1690b33aa10acb955f`; the same harness was compiled against
the baseline and the implementation using SDK 10.0.114. The runtime versions
were .NET 8.0.33 and .NET 10.0.14.

These measurements used `DOTNET_TieredCompilation=0`, 3 warmup batches, and the
median CPU duration of 9 measured batches. Baseline and implementation processes
ran sequentially on the same Linux ARM64 host. Separate exploratory runs with
default tiering showed compilation transitions in some workloads; their medians
are unsuitable as steady-state evidence and are not the numbers below.

| Runtime | Workload | Baseline CPU per batch | Implementation CPU per batch | Change |
| --- | --- | ---: | ---: | ---: |
| .NET 8.0.33 | 8 NOP calls | 9.59392 ms | 8.10172 ms | -15.55% |
| .NET 8.0.33 | 8 arithmetic calls | 21.82864 ms | 20.51248 ms | -6.03% |
| .NET 10.0.14 | 8 NOP calls | 7.44908 ms | 9.09436 ms | +22.09% |
| .NET 10.0.14 | 8 arithmetic calls | 19.42192 ms | 20.83860 ms | +7.29% |

The .NET 8 speedups are specific to these interpreter builds and workloads.
They do not establish that the accounting has no cost. The .NET 10 results show
a material remaining cost, and the difference across runtimes is a reason to
measure the actual Unity target. The generous budget lets the wall gate avoid
most checkpoint CPU reads; near-budget confirmation and concurrent activity can
cost more.

Short callbacks expose entry/exit and clock-acquisition overhead:

| Runtime | Baseline CPU per `ret` callback | Implementation CPU per callback | Added CPU per callback |
| --- | ---: | ---: | ---: |
| .NET 8.0.33 | 1,126.004 ns | 2,935.220 ns | 1,809.216 ns |
| .NET 10.0.14 | 1,135.544 ns | 3,039.684 ns | 1,904.140 ns |

The full callback measurement includes interpreter boundary behavior as well as
CPU-clock acquisition, sampling, and accounting. It does not isolate a single
clock read or promise this cost on Windows.

The isolated read loops used the implementation's exact bound Linux CPU reader:

| Runtime | `Stopwatch.GetTimestamp()` CPU cost | Bound CPU-wrapper read CPU cost | Ratio |
| --- | ---: | ---: | ---: |
| .NET 8.0.33 | 31.536 ns | 361.203 ns | 11.45× |
| .NET 10.0.14 | 32.537 ns | 380.402 ns | 11.69× |

These are whole loop/call costs, including wrapper dispatch and checksum work,
without subtraction. The JSON also records elapsed wall time independently.
The difference is approximately 5.2–5.4 ns per interpreted instruction if paid
at every 64-instruction checkpoint; the guarded implementation avoids paying
the CPU-read cost on most generous-budget checkpoints.

This evidence comes from a **CoreCLR console with Unity stubs**. It does not
validate a Unity CoreCLR player, Unity Mono, IL2CPP, Windows DLL deployment,
Android devices, or clock precision on other hardware. Those target runs remain
required before claiming support for those environments.
