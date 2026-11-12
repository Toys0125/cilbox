using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cilbox;

namespace TestCilboxCpuBudget
{
    internal static class AccountingTests
    {
        private sealed class Counter { internal long Cpu; internal bool Fail; }
        private sealed class ManualReader : ICilboxThreadCpuClock
        {
            internal readonly Counter Counter;
            internal bool Disposed;
            internal ManualReader(Counter counter, bool isCpuTime) { Counter = counter; IsCpuTime = isCpuTime; }
            public string Name => IsCpuTime ? "deterministic per-thread CPU" : "deterministic wall fallback";
            public bool IsCpuTime { get; }
            public bool TryReadNanoseconds(out long value)
            {
                value = Interlocked.Read(ref Counter.Cpu);
                return !Counter.Fail && !Disposed;
            }
            public void Dispose() { Disposed = true; }
        }

        private sealed class ManualClocks : IDisposable
        {
            private readonly Func<ICilboxThreadCpuClock> oldFactory = CilboxExecutionBudget.ClockFactory;
            private readonly Action oldPreparation = CilboxExecutionBudget.ClockPreparation;
            private readonly Func<long> oldWall = CilboxExecutionBudget.WallTimestampProvider;
            private readonly long oldFrequency = CilboxExecutionBudget.WallFrequency;
            private readonly ConcurrentDictionary<int, Counter> counters = new();
            private readonly ConcurrentBag<ManualReader> readers = new();
            private long wall = 1_000_000_000;
            internal Counter Current => counters.GetOrAdd(Environment.CurrentManagedThreadId, _ => new Counter());
            internal bool UseCpuMode = true;
            internal int OpenedReaderCount => readers.Count;

            internal ManualClocks()
            {
                CilboxExecutionBudget.ClockFactory = () => {
                    var reader = new ManualReader(Current, UseCpuMode);
                    readers.Add(reader);
                    return reader;
                };
                CilboxExecutionBudget.WallTimestampProvider = () => Interlocked.Read(ref wall);
                CilboxExecutionBudget.WallFrequency = 1_000_000_000;
            }
            internal void Cpu(long nanoseconds)
            {
                Interlocked.Add(ref Current.Cpu, nanoseconds);
                Interlocked.Add(ref wall, nanoseconds);
            }
            internal void Wait(long nanoseconds) => Interlocked.Add(ref wall, nanoseconds);
            public void Dispose()
            {
                CilboxExecutionBudget.ClockPreparation = oldPreparation;
                CilboxExecutionBudget.ClockFactory = oldFactory;
                CilboxExecutionBudget.WallTimestampProvider = oldWall;
                CilboxExecutionBudget.WallFrequency = oldFrequency;
                Program.Require(readers.All(x => x.Disposed), "A deterministic CPU reader was not disposed.");
                Program.Require(CilboxExecutionBudget.CurrentThreadState.Scopes.Count == 0,
                    "Deterministic test left main-thread accounting scopes active.");
            }
        }

        private static CilboxMethod Empty(TestCilbox.CilboxTester owner, string name = "Manual") =>
            Program.Method(owner, name, new byte[] { 0x2a });

        private static CilboxCpuBudgetTimeoutException ExpectTimeout(Action action, TestCilbox.CilboxTester owner)
        {
            try { action(); }
            catch (CilboxCpuBudgetTimeoutException error)
            {
                Program.Require(ReferenceEquals(error.Owner, owner), "Timeout attributed to the wrong owner.");
                return error;
            }
            throw new InvalidOperationException("Expected deterministic CPU budget timeout.");
        }

        public static void Run(Action<string, Action> run)
        {
            run("preparation_serializes_concurrent_admission_before_any_active_scope", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox(100_000);
                var method = Empty(owner);
                using var requested = new CountdownEvent(2);
                using var preparing = new ManualResetEventSlim();
                using var releasePreparation = new ManualResetEventSlim();
                using var admitted = new CountdownEvent(2);
                using var finish = new ManualResetEventSlim();
                var failures = new ConcurrentQueue<Exception>();
                int preparations = 0;
                var preparation = new CilboxClockPreparation(() => {
                    Interlocked.Increment(ref preparations);
                    preparing.Set();
                    Program.Require(releasePreparation.Wait(TimeSpan.FromSeconds(5)),
                        "Preparation release barrier timed out.");
                });
                CilboxExecutionBudget.ClockPreparation = () => {
                    requested.Signal();
                    preparation.EnsurePrepared();
                };
                ThreadStart worker = () => {
                    bool entered = false;
                    try
                    {
                        owner.ExecutionBudget.Enter(method);
                        entered = true;
                        admitted.Signal();
                        Program.Require(finish.Wait(TimeSpan.FromSeconds(5)),
                            "Prepared worker finish barrier timed out.");
                    }
                    catch (Exception error) { failures.Enqueue(error); }
                    finally { if (entered) owner.ExecutionBudget.Exit(false); }
                };
                Thread[] workers = {
                    new Thread(worker) { IsBackground = true },
                    new Thread(worker) { IsBackground = true }
                };
                foreach (Thread workerThread in workers) workerThread.Start();
                try
                {
                    Program.Require(requested.Wait(TimeSpan.FromSeconds(5)) &&
                        preparing.Wait(TimeSpan.FromSeconds(5)),
                        "Workers did not reach preparation before admission.");
                    Program.Require(SpinWait.SpinUntil(() => workers.All(workerThread =>
                        (workerThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0),
                        TimeSpan.FromSeconds(5)), "Preparation workers did not reach a blocked state.");
                    Program.Require(owner.ExecutionBudget.ActiveDepth == 0 &&
                        clock.OpenedReaderCount == 0 && admitted.CurrentCount == 2,
                        "A reader or active scope was created before preparation completed.");
                    Program.Require(Volatile.Read(ref preparations) == 1,
                        "Concurrent admission ran preparation more than once.");
                    releasePreparation.Set();
                    Program.Require(admitted.Wait(TimeSpan.FromSeconds(5)),
                        "Workers did not enter after preparation completed.");
                    Program.Require(owner.ExecutionBudget.ActiveDepth == 2 &&
                        clock.OpenedReaderCount == 2 && Volatile.Read(ref preparations) == 1,
                        "Prepared concurrent owners did not get separate readers.");
                }
                finally
                {
                    releasePreparation.Set();
                    finish.Set();
                    foreach (Thread workerThread in workers)
                        Program.Require(workerThread.Join(5000), "Preparation worker failed to exit.");
                }
                Program.Require(failures.IsEmpty, "Preparation worker failed: " + string.Join("; ", failures));
                Program.Require(owner.ExecutionBudget.ActiveDepth == 0,
                    "Concurrent preparation left an active accounting scope.");
            });

            run("preparation_cpu_precedes_budget_and_nested_entry_does_not_repeat_it", () => {
                using var clock = new ManualClocks();
                var a = Program.NewBox();
                var b = Program.NewBox();
                var am = Empty(a, "PreparedA");
                var bm = Empty(b, "PreparedB");
                int preparations = 0;
                var preparation = new CilboxClockPreparation(() => {
                    ++preparations;
                    Program.Require(CilboxExecutionBudget.CurrentThreadState.Scopes.Count == 0,
                        "Preparation ran inside an active call.");
                    clock.Cpu(8_000_000);
                });
                CilboxExecutionBudget.ClockPreparation = preparation.EnsurePrepared;
                a.ExecutionBudget.Enter(am);
                try
                {
                    clock.Cpu(1_000_000);
                    b.ExecutionBudget.Enter(bm);
                    try { clock.Cpu(1_000_000); }
                    finally { b.ExecutionBudget.Exit(false); }
                }
                finally { a.ExecutionBudget.Exit(false); }
                a.ExecutionBudget.Enter(am);
                a.ExecutionBudget.Exit(false);
                Program.Require(preparations == 1,
                    "Nested or later entry repeated the blocking preparation.");
                Program.Require(a.ExecutionBudget.CurrentFrameCpuNanoseconds == 2_000_000 &&
                    b.ExecutionBudget.CurrentFrameCpuNanoseconds == 1_000_000,
                    "Initialization CPU was charged to a call, or nested CPU was lost.");
            });

            run("preparation_terminal_fallback_never_retries_or_changes_an_active_clock", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox(100_000);
                var method = Empty(owner);
                int preparations = 0;
                bool laterQualificationWouldPass = false;
                var preparation = new CilboxClockPreparation(() => {
                    ++preparations;
                    clock.UseCpuMode = laterQualificationWouldPass;
                });
                CilboxExecutionBudget.ClockPreparation = preparation.EnsurePrepared;
                owner.ExecutionBudget.Enter(method);
                try
                {
                    laterQualificationWouldPass = true;
                    clock.Cpu(1_000_000);
                    owner.ExecutionBudget.Enter(method);
                    try { clock.Cpu(1_000_000); }
                    finally { owner.ExecutionBudget.Exit(false); }
                    Program.Require(clock.OpenedReaderCount == 1 &&
                        owner.ExecutionBudget.ClockDescription.Contains("wall fallback"),
                        "Nested admission replaced or relabeled its active fallback reader.");
                }
                finally { owner.ExecutionBudget.Exit(false); }
                owner.ExecutionBudget.Enter(method);
                owner.ExecutionBudget.Exit(false);
                Program.Require(preparations == 1 && !clock.UseCpuMode &&
                    owner.ExecutionBudget.ClockDescription.Contains("wall fallback"),
                    "A terminal fallback was retried on a nested or later call.");
                Program.Require(owner.ExecutionBudget.CurrentFrameCpuNanoseconds == 2_000_000,
                    "Terminal fallback preparation altered the settled accounting interval.");
            });

            run("preparation_exception_is_cached_and_rejects_admission_without_retry", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var method = Empty(owner);
                var original = new InvalidOperationException("Expected preparation failure.");
                int preparations = 0;
                var preparation = new CilboxClockPreparation(() => {
                    ++preparations;
                    throw original;
                });
                CilboxExecutionBudget.ClockPreparation = preparation.EnsurePrepared;
                for (int attempt = 0; attempt < 3; ++attempt)
                {
                    Exception? observed = null;
                    try { owner.ExecutionBudget.Enter(method); }
                    catch (Exception error) { observed = error; }
                    Program.Require(ReferenceEquals(observed, original),
                        "Preparation did not preserve its terminal exception.");
                    Program.Require(owner.ExecutionBudget.ActiveDepth == 0 &&
                        CilboxExecutionBudget.CurrentThreadState.Scopes.Count == 0 &&
                        clock.OpenedReaderCount == 0,
                        "Failed preparation admitted a call or opened a reader.");
                }
                Program.Require(preparations == 1, "Terminal preparation exception was retried.");
            });

            run("preparation_reentrant_entry_is_rejected_before_activation", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var method = Empty(owner);
                int preparations = 0;
                var preparation = new CilboxClockPreparation(() => {
                    ++preparations;
                    if (preparations > 1)
                        throw new InvalidOperationException("Preparation callback was entered recursively.");
                    owner.ExecutionBudget.Enter(method);
                });
                CilboxExecutionBudget.ClockPreparation = preparation.EnsurePrepared;
                Exception? original = null;
                for (int attempt = 0; attempt < 2; ++attempt)
                {
                    Exception? observed = null;
                    try { owner.ExecutionBudget.Enter(method); }
                    catch (Exception error) { observed = error; }
                    Program.Require(observed is InvalidOperationException &&
                        observed.Message.Contains("Cannot enter Cilbox during CPU clock preparation."),
                        "Recursive initialization did not reject entry with its preparation diagnostic.");
                    if (attempt == 0) original = observed;
                    else Program.Require(ReferenceEquals(observed, original),
                        "Recursive preparation failure was not cached.");
                }
                Program.Require(preparations == 1 && clock.OpenedReaderCount == 0 &&
                    owner.ExecutionBudget.ActiveDepth == 0 &&
                    CilboxExecutionBudget.CurrentThreadState.Scopes.Count == 0,
                    "Recursive preparation created a reader, repeated initialization, or leaked a scope.");
            });

            run("deterministic_same_owner_recursion_counts_once", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox(100_000);
                var budget = owner.ExecutionBudget;
                var method = Empty(owner);
                budget.Enter(method);
                try
                {
                    clock.Cpu(1_000_000);
                    budget.Enter(method);
                    try { clock.Cpu(2_000_000); }
                    finally { budget.Exit(false); }
                    clock.Cpu(1_000_000);
                }
                finally { budget.Exit(false); }
                Program.Require(budget.CurrentFrameCpuNanoseconds == 4_000_000,
                    "A -> A must charge 4 ms once, not duplicate its inner 2 ms.");
                Program.Require(budget.ActiveDepth == 0, "Recursive depth did not return to zero.");
            });

            run("deterministic_nested_distinct_owners_are_inclusive_without_self_duplication", () => {
                using var clock = new ManualClocks();
                var a = Program.NewBox(100_000);
                var b = Program.NewBox(100_000);
                var am = Empty(a, "A");
                var bm = Empty(b, "B");
                a.ExecutionBudget.Enter(am);
                try
                {
                    clock.Cpu(1_000_000);
                    b.ExecutionBudget.Enter(bm);
                    try
                    {
                        clock.Cpu(2_000_000);
                        a.ExecutionBudget.Enter(am);
                        try { clock.Cpu(1_000_000); }
                        finally { a.ExecutionBudget.Exit(false); }
                        clock.Cpu(1_000_000);
                    }
                    finally { b.ExecutionBudget.Exit(false); }
                    clock.Cpu(1_000_000);
                }
                finally { a.ExecutionBudget.Exit(false); }
                Program.Require(a.ExecutionBudget.CurrentFrameCpuNanoseconds == 6_000_000,
                    "A -> B -> A should charge A exactly 6 ms.");
                Program.Require(b.ExecutionBudget.CurrentFrameCpuNanoseconds == 4_000_000,
                    "Inclusive B charge should be exactly 4 ms.");
            });

            run("deterministic_wait_rebuilds_gate_without_losing_prior_cpu", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var budget = owner.ExecutionBudget;
                var method = Empty(owner);
                budget.Enter(method);
                try
                {
                    clock.Cpu(4_000_000);
                    clock.Wait(50_000_000);
                    CilboxExecutionBudget.Checkpoint(method, 64);
                    Program.Require(budget.CurrentFrameCpuNanoseconds == 4_000_000,
                        "Wall wait must preserve exactly the prior 4 ms CPU.");
                    clock.Cpu(1_200_000);
                    ExpectTimeout(() => CilboxExecutionBudget.Checkpoint(method, 128), owner);
                }
                finally { budget.Exit(false); }
            });

            run("deterministic_active_frame_reset_splits_each_cpu_interval_once", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var budget = owner.ExecutionBudget;
                var method = Empty(owner);
                budget.Enter(method);
                try
                {
                    clock.Cpu(2_000_000);
                    Program.Require(budget.ResetFrame() == 2_000_000, "Old frame lost CPU.");
                    Program.Require(budget.CurrentFrameCpuNanoseconds == 0, "New frame was not reset.");
                    clock.Cpu(1_000_000);
                }
                finally { budget.Exit(false); }
                Program.Require(budget.CurrentFrameCpuNanoseconds == 1_000_000,
                    "Active reset duplicated or dropped the new frame's interval.");
            });

            run("deterministic_active_overrun_survives_frame_reset", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var budget = owner.ExecutionBudget;
                var method = Empty(owner);
                budget.Enter(method);
                try
                {
                    clock.Cpu(6_000_000);
                    Program.Require(budget.ResetFrame() == 6_000_000, "Frame snapshot lost overrun CPU.");
                    owner.timeoutLengthUs = 100_000;
                    CilboxCpuBudgetTimeoutException error = ExpectTimeout(() => CilboxExecutionBudget.Checkpoint(method, 64), owner);
                    Program.Require(error.Message.Contains("6000/5000 us"),
                        "Reset or limit update replaced the original timeout charge and allowance.");
                }
                finally { budget.Exit(false); }
            });

            run("deterministic_exception_unwind_records_cpu_without_replacing_original", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var method = Empty(owner);
                Exception original = new InvalidOperationException("original failure");
                Exception? observed = null;
                owner.ExecutionBudget.Enter(method);
                try
                {
                    try { clock.Cpu(6_000_000); throw original; }
                    finally { owner.ExecutionBudget.Exit(false); }
                }
                catch (Exception error) { observed = error; }
                Program.Require(ReferenceEquals(original, observed), "Unwind replaced the original exception.");
                Program.Require(owner.ExecutionBudget.ActiveDepth == 0 &&
                    owner.ExecutionBudget.CurrentFrameCpuNanoseconds == 6_000_000,
                    "Unwind failed to settle or remove the active scope.");
            });

            run("deterministic_clock_failure_is_not_mixed_with_wall_origin", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var method = Empty(owner);
                owner.ExecutionBudget.Enter(method);
                try
                {
                    clock.Cpu(1_000_000);
                    clock.Current.Fail = true;
                    clock.Wait(50_000_000);
                    Exception? failure = null;
                    try { CilboxExecutionBudget.Checkpoint(method, 64); }
                    catch (Exception error) { failure = error; }
                    Program.Require(failure is InvalidOperationException &&
                        failure is not CilboxCpuBudgetTimeoutException,
                        "Clock failure must be diagnosed without fabricating a wall-time CPU timeout.");
                }
                finally { owner.ExecutionBudget.Exit(false); }
            });

            run("deterministic_mixed_clock_history_is_reported_after_sequential_activations", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox(100_000);
                var method = Empty(owner);
                var budget = owner.ExecutionBudget;
                clock.UseCpuMode = false;
                budget.Enter(method);
                try { clock.Cpu(1_000_000); }
                finally { budget.Exit(false); }
                Program.Require(budget.ClockDescription.Contains("wall fallback"),
                    "Fallback activation must expose wall-time accounting.");
                clock.UseCpuMode = true;
                budget.Enter(method);
                try { clock.Cpu(2_000_000); }
                finally { budget.Exit(false); }
                Program.Require(budget.CurrentFrameCpuNanoseconds == 3_000_000,
                    "Switching readers must retain both settled activation charges.");
                Program.Require(budget.ClockDescription.Contains("Mixed execution clocks"),
                    "A later CPU activation must not relabel earlier wall charges as CPU-only.");
                Program.Require(budget.ResetFrame() == 3_000_000, "Mixed frame report lost its charge.");
                Program.Require(budget.ClockDescription.Contains("Mixed execution clocks"),
                    "Reset must not mislabel the mixed completed frame.");
            });

            run("deterministic_zero_and_negative_budgets_reject_admission", () => {
                using var clock = new ManualClocks();
                foreach (long limit in new long[] { 0, -1 })
                {
                    var owner = Program.NewBox(limit);
                    ExpectTimeout(() => owner.ExecutionBudget.Enter(Empty(owner)), owner);
                    Program.Require(owner.ExecutionBudget.ActiveDepth == 0,
                        "Rejected admission leaked accounting depth.");
                }
            });

            run("deterministic_concurrent_owner_charge_uses_both_threads", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox();
                var method = Empty(owner);
                using var ready = new CountdownEvent(2);
                using var check = new ManualResetEventSlim();
                var failures = new ConcurrentQueue<Exception>();
                int timeouts = 0;
                ThreadStart worker = () => {
                    bool entered = false;
                    try
                    {
                        owner.ExecutionBudget.Enter(method);
                        entered = true;
                        clock.Cpu(3_000_000);
                        ready.Signal();
                        Program.Require(check.Wait(TimeSpan.FromSeconds(5)), "Checkpoint barrier timed out.");
                        try { CilboxExecutionBudget.Checkpoint(method, 64); }
                        catch (CilboxCpuBudgetTimeoutException error)
                        {
                            Program.Require(ReferenceEquals(error.Owner, owner), "Concurrent timeout owner mismatch.");
                            Interlocked.Increment(ref timeouts);
                        }
                    }
                    catch (Exception error) { failures.Enqueue(error); }
                    finally { if (entered) owner.ExecutionBudget.Exit(false); }
                };
                Thread[] threads = { new Thread(worker) { IsBackground = true }, new Thread(worker) { IsBackground = true } };
                foreach (Thread thread in threads) thread.Start();
                bool reached = ready.Wait(TimeSpan.FromSeconds(5));
                check.Set();
                foreach (Thread thread in threads) Program.Require(thread.Join(5000), "Concurrent worker failed to exit.");
                Program.Require(reached, "Workers failed to reach concurrent checkpoint barrier.");
                Program.Require(failures.IsEmpty, "Concurrent worker failed: " + string.Join("; ", failures));
                Program.Require(timeouts > 0, "Aggregate 6 ms on two threads escaped a shared 5 ms budget.");
                Program.Require(owner.ExecutionBudget.ActiveDepth == 0 &&
                    owner.ExecutionBudget.CurrentFrameCpuNanoseconds == 6_000_000,
                    "Concurrent final CPU accounting duplicated or dropped an interval.");
            });

            run("deterministic_frame_reset_snapshots_active_foreign_threads", () => {
                using var clock = new ManualClocks();
                var owner = Program.NewBox(100_000);
                var method = Empty(owner);
                using var ready = new CountdownEvent(2);
                using var advance = new ManualResetEventSlim();
                using var advanced = new CountdownEvent(2);
                using var finish = new ManualResetEventSlim();
                var failures = new ConcurrentQueue<Exception>();
                ThreadStart worker = () => {
                    bool entered = false;
                    try
                    {
                        owner.ExecutionBudget.Enter(method);
                        entered = true;
                        clock.Cpu(2_000_000);
                        ready.Signal();
                        Program.Require(advance.Wait(TimeSpan.FromSeconds(5)), "Advance barrier timed out.");
                        clock.Cpu(1_000_000);
                        advanced.Signal();
                        Program.Require(finish.Wait(TimeSpan.FromSeconds(5)), "Finish barrier timed out.");
                    }
                    catch (Exception error) { failures.Enqueue(error); }
                    finally { if (entered) owner.ExecutionBudget.Exit(false); }
                };
                Thread[] threads = { new Thread(worker) { IsBackground = true }, new Thread(worker) { IsBackground = true } };
                foreach (Thread thread in threads) thread.Start();
                long first = -1, second = -1;
                bool firstReady = ready.Wait(TimeSpan.FromSeconds(5));
                try
                {
                    if (firstReady) first = owner.ExecutionBudget.ResetFrame();
                    advance.Set();
                    if (advanced.Wait(TimeSpan.FromSeconds(5))) second = owner.ExecutionBudget.ResetFrame();
                }
                finally
                {
                    advance.Set();
                    finish.Set();
                    foreach (Thread thread in threads) Program.Require(thread.Join(5000), "Frame worker failed to exit.");
                }
                Program.Require(firstReady && failures.IsEmpty, "Frame worker failed: " + string.Join("; ", failures));
                Program.Require(first == 4_000_000 && second == 2_000_000,
                    $"Foreign frame snapshots should be 4 ms and 2 ms, got {first} ns and {second} ns.");
                Program.Require(owner.ExecutionBudget.CurrentFrameCpuNanoseconds == 0 &&
                    owner.ExecutionBudget.ActiveDepth == 0, "Exit recounted CPU already published by frame reset.");
            });
        }
    }
}
