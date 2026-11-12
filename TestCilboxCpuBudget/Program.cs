using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Cilbox;

namespace TestCilboxCpuBudget
{
    internal static class CpuMeasurement
    {
#if BASELINE
        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec { public long Seconds; public long Nanoseconds; }
        [DllImport("libc", EntryPoint = "clock_gettime")]
        private static extern int ClockGetTime(int id, out Timespec value);
        public static string Name => "Linux CLOCK_THREAD_CPUTIME_ID (baseline instrumentation)";
        public static bool IsCpuTime => OperatingSystem.IsLinux() && IntPtr.Size == 8;
        public static long ReadNanoseconds()
        {
            if (!IsCpuTime || ClockGetTime(3, out Timespec value) != 0)
                throw new PlatformNotSupportedException("Baseline timing requires 64-bit Linux.");
            return value.Seconds * 1_000_000_000L + value.Nanoseconds;
        }
        public static void Dispose() { }
#else
        [ThreadStatic] private static ICilboxThreadCpuClock? reader;
        private static ICilboxThreadCpuClock Reader => reader ??= CilboxThreadCpuClock.OpenCurrentThread();
        public static string Name => Reader.Name;
        public static bool IsCpuTime => Reader.IsCpuTime;
        public static long ReadNanoseconds()
        {
            if (!Reader.IsCpuTime || !Reader.TryReadNanoseconds(out long value))
                throw new InvalidOperationException("A working precise thread CPU clock is required: " + Reader.Name);
            return value;
        }
        public static void Dispose() { reader?.Dispose(); reader = null; }
#endif
    }

    public static class BudgetHost
    {
        [DllImport("cilboxbudgetprobe", EntryPoint = "cilbox_budget_burn_ns")]
        private static extern void NativeBurn(ulong nanoseconds);
        private static long sink;
        public static int Calls;
        public static Action? NestedAction;
        public static int Caught;
        public static void MarkCaught() => ++Caught;

        private static void Burn(long nanoseconds)
        {
            ++Calls;
            if (OperatingSystem.IsLinux())
            {
                NativeBurn((ulong)nanoseconds);
                return;
            }
            long begin = CpuMeasurement.ReadNanoseconds();
            ulong value = 0x123456789abcdefUL;
            do
            {
                for (int i = 0; i < 256; ++i) { value ^= value << 13; value ^= value >> 7; value ^= value << 17; }
            } while (CpuMeasurement.ReadNanoseconds() - begin < nanoseconds);
            Volatile.Write(ref sink, unchecked((long)value));
        }

        public static void Cpu2() => Burn(2_000_000);
        public static void Cpu4() => Burn(4_000_000);
        public static void Cpu12() => Burn(12_000_000);
        public static void Sleep50() { ++Calls; Thread.Sleep(50); }
        public static void ThrowNow() => throw new InvalidOperationException("intentional host exception");
        public static void InvokeNested() => NestedAction!();
    }

    public static class Program
    {
        private const int WorkInstructions = 262144;
        private const int BatchCalls = 8;
        private record TestResult(string name, bool passed, string? error);
        private static readonly List<TestResult> results = new();

        internal record Invocation(double CpuMs, double WallMs, Exception? Exception);

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        internal static TestCilbox.CilboxTester NewBox(long budgetUs = 5000)
        {
            var box = new TestCilbox.CilboxTester();
            box.timeoutLengthUs = budgetUs;
            box.classes = new Dictionary<string, int>();
            box.classesList = Array.Empty<CilboxClass>();
            return box;
        }

        internal static CilboxMethod Method(TestCilbox.CilboxTester box, string name, byte[] body)
        {
            var method = new CilboxMethod();
            method.Load(new CilboxClass { box = box, className = "CpuBudgetRegression" },
                new SerializedMethod {
                    methodName = name, body = body, maxStack = 8, isVoid = true,
                    isStatic = true, isCtor = false, fullSignature = "Void " + name + "()",
                    locals = Array.Empty<SerializedField>(), parameters = Array.Empty<SerializedField>(),
                    exceptionHandlers = Array.Empty<SerializedExceptionHandler>()
                });
            return method;
        }

        internal static CilboxMethod HostMethod(TestCilbox.CilboxTester box, string hostMethod, int nops = 0)
        {
            box.metadatas = new [] {
                new CilMetadataTokenInfo(MetaTokenType.mtMethod) {
                    isValid = true, isNative = true,
                    nativeMethod = typeof(BudgetHost).GetMethod(hostMethod)!,
                    nativeParameterTypes = Array.Empty<Type>(), nativeIsVoid = true,
                    Name = hostMethod, declaringTypeName = typeof(BudgetHost).FullName
                }
            };
            byte[] body = new byte[6 + nops];
            body[0] = 0x28;
            body[^1] = 0x2a;
            return Method(box, hostMethod, body);
        }

        internal static Invocation Invoke(TestCilbox.CilboxTester box, CilboxMethod method, object[]? arguments = null)
        {
            long wall0 = Stopwatch.GetTimestamp();
            long cpu0 = CpuMeasurement.ReadNanoseconds();
            Exception? failure = null;
            try { method.Interpret(null, arguments ?? Array.Empty<object>()); }
            catch (Exception e) { failure = e; }
            long cpu1 = CpuMeasurement.ReadNanoseconds();
            long wall1 = Stopwatch.GetTimestamp();
            return new Invocation((cpu1 - cpu0) / 1e6,
                (wall1 - wall0) * 1000.0 / Stopwatch.Frequency, failure);
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                results.Add(new TestResult(name, true, null));
            }
            catch (Exception error)
            {
                results.Add(new TestResult(name, false, error.ToString()));
            }
        }

#if !BASELINE
        private static void RequireTimeout(TestCilbox.CilboxTester box, Invocation call)
        {
            Require(call.Exception is CilboxCpuBudgetTimeoutException,
                "Expected a CPU budget timeout, got " + (call.Exception?.GetType().Name ?? "normal return"));
            Require(box.disabled, "Over-budget owner must be disabled.");
            Require(box.ExecutionBudget.ActiveDepth == 0, "Timeout must unwind the accounting stack.");
        }

        private static void WarmRuntime()
        {
            foreach (string host in new[] { nameof(BudgetHost.Cpu2), nameof(BudgetHost.Cpu4),
                nameof(BudgetHost.Cpu12), nameof(BudgetHost.Sleep50), nameof(BudgetHost.ThrowNow) })
            {
                for (int i = 0; i < 3; ++i)
                {
                    var box = NewBox(2_000_000);
                    Invoke(box, HostMethod(box, host, 64));
                }
            }
            for (int i = 0; i < 3; ++i)
            {
                var box = NewBox();
                Invoke(box, Method(box, "WarmTimeout", new byte[] { 0x2b, 0xfe }));
            }
        }

        private static CilMetadataTokenInfo HostToken(string name) =>
            new CilMetadataTokenInfo(MetaTokenType.mtMethod) {
                isValid = true, isNative = true, nativeMethod = typeof(BudgetHost).GetMethod(name)!,
                nativeParameterTypes = Array.Empty<Type>(), nativeIsVoid = true,
                Name = name, declaringTypeName = typeof(BudgetHost).FullName
            };

        private static CilboxMethod CatchingHostMethod(TestCilbox.CilboxTester box, string host)
        {
            box.metadatas = new[] { HostToken(host), HostToken(nameof(BudgetHost.MarkCaught)) };
            byte[] body = { 0x28, 0, 0, 0, 0, 0xde, 8, 0x26, 0x28, 1, 0, 0, 0, 0xde, 0, 0x2a };
            var method = new CilboxMethod();
            method.Load(new CilboxClass { box = box, className = "CpuBudgetCatchRegression" },
                new SerializedMethod {
                    methodName = "CatchHost", body = body, maxStack = 8, isVoid = true,
                    isStatic = true, isCtor = false, fullSignature = "Void CatchHost()",
                    locals = Array.Empty<SerializedField>(), parameters = Array.Empty<SerializedField>(),
                    exceptionHandlers = new[] { new SerializedExceptionHandler {
                        flags = 0, tryOffset = 0, tryLength = 7, handlerOffset = 7, handlerLength = 8,
                        hasCatchType = true, catchType = new SerializedTypeDescriptor {
                            assemblyName = typeof(Exception).Assembly.GetName().Name,
                            typeName = typeof(Exception).FullName
                        }
                    }}
                });
            return method;
        }

        private static (TestCilbox.CilboxTester Parent, TestCilbox.CilboxTester Child,
            CilboxMethod Caller, CilboxProxy Receiver) CrossBoxVirtualFixture(long parentUs, long childUs, string host)
        {
            var parent = NewBox(parentUs);
            var child = NewBox(childUs);
            var parentClass = new CilboxClass { box = parent, className = "VirtualBase" };
            var childClass = new CilboxClass { box = child, className = "VirtualDerived" };
            CilboxMethod baseMethod = Method(parent, "VirtualCall", new byte[] { 0x2a });
            baseMethod.isStatic = false;
            baseMethod.parentClass = parentClass;
            CilboxMethod overrideMethod = HostMethod(child, host);
            overrideMethod.isStatic = false;
            overrideMethod.parentClass = childClass;
            overrideMethod.fullSignature = baseMethod.fullSignature;
            parentClass.methods = new[] { baseMethod };
            childClass.methods = new[] { overrideMethod };
            parentClass.methodFullSignatureToIndex = new Dictionary<string, uint> { [baseMethod.fullSignature] = 0 };
            childClass.methodFullSignatureToIndex = new Dictionary<string, uint> { [baseMethod.fullSignature] = 0 };
            parent.classesList = new[] { parentClass };
            child.classesList = new[] { childClass };
            parent.classes[parentClass.className] = 0;
            child.classes[childClass.className] = 0;
            parent.metadatas = new[] {
                new CilMetadataTokenInfo(MetaTokenType.mtMethod) {
                    isValid = true, isNative = false, interpretiveMethod = 0, interpretiveMethodClass = 0
                },
                HostToken(nameof(BudgetHost.MarkCaught))
            };
            var caller = new CilboxMethod();
            caller.Load(parentClass, new SerializedMethod {
                methodName = "VirtualCatch", maxStack = 8, isVoid = true, isStatic = true,
                fullSignature = "Void VirtualCatch(Object)",
                body = new byte[] { 0x02, 0x6f, 0, 0, 0, 0, 0xde, 8, 0x26, 0x28, 1, 0, 0, 0, 0xde, 0, 0x2a },
                locals = Array.Empty<SerializedField>(),
                parameters = new[] { new SerializedField { name = "receiver", type = new SerializedTypeDescriptor {
                    assemblyName = typeof(object).Assembly.GetName().Name, typeName = typeof(object).FullName
                }}},
                exceptionHandlers = new[] { new SerializedExceptionHandler {
                    flags = 0, tryOffset = 0, tryLength = 8, handlerOffset = 8, handlerLength = 8,
                    hasCatchType = true, catchType = new SerializedTypeDescriptor {
                        assemblyName = typeof(Exception).Assembly.GetName().Name, typeName = typeof(Exception).FullName
                    }
                }}
            });
            var receiver = new CilboxProxy { box = child, cls = childClass,
                className = childClass.className, fields = Array.Empty<StackElement>() };
            return (parent, child, caller, receiver);
        }

        private static void ActualInterpreterTests()
        {
            Run("wait_does_not_consume_cpu_budget", () => {
                for (int i = 0; i < 3; ++i)
                {
                    var box = NewBox();
                    Invocation call = Invoke(box, HostMethod(box, nameof(BudgetHost.Sleep50), 64));
                    Require(call.Exception == null && !box.disabled, "A warmed 50 ms wait must not timeout.");
                    Require(call.WallMs >= 40, "Wait did not produce the expected elapsed interval.");
                    Require(box.ExecutionBudget.CurrentFrameCpuNanoseconds < 5_000_000,
                        "Wait charge unexpectedly reached the CPU budget.");
                    Require(box.ExecutionBudget.ActiveDepth == 0, "Wait scope did not exit.");
                }
            });

            Run("cpu_heavy_interpreted_loop_times_out", () => {
                var box = NewBox();
                RequireTimeout(box, Invoke(box, Method(box, "Infinite", new byte[] { 0x2b, 0xfe })));
            });

            Run("synchronous_host_cpu_then_return_times_out", () => {
                var box = NewBox();
                RequireTimeout(box, Invoke(box, HostMethod(box, nameof(BudgetHost.Cpu12))));
                Require(box.ExecutionBudget.CurrentFrameCpuNanoseconds >= 5_000_000,
                    "Synchronous host CPU was not included.");
            });

            Run("short_calls_share_frame_budget_and_normal_returns_are_checked", () => {
                var box = NewBox();
                CilboxMethod method = HostMethod(box, nameof(BudgetHost.Cpu2));
                Require(Invoke(box, method).Exception == null, "First 2 ms call should fit.");
                Require(Invoke(box, method).Exception == null, "Second 2 ms call should fit.");
                RequireTimeout(box, Invoke(box, method));
                Require(box.ExecutionBudget.CurrentFrameCpuNanoseconds >= 6_000_000,
                    "Cumulative charge lost CPU from previous calls.");
                int calls = BudgetHost.Calls;
                method.Interpret(null, Array.Empty<object>());
                Require(BudgetHost.Calls == calls, "Disabled owner executed another host call.");
            });

            Run("cpu_before_wait_remains_charged_after_wait", () => {
                var box = NewBox();
                Require(Invoke(box, HostMethod(box, nameof(BudgetHost.Cpu2))).Exception == null,
                    "Initial 2 ms call should fit.");
                long before = box.ExecutionBudget.CurrentFrameCpuNanoseconds;
                Require(Invoke(box, HostMethod(box, nameof(BudgetHost.Sleep50))).Exception == null,
                    "Wait falsely exhausted the remaining CPU allowance.");
                long after = box.ExecutionBudget.CurrentFrameCpuNanoseconds;
                Require(after >= before && after < 5_000_000, "Wait reset or inflated prior CPU charge.");
                RequireTimeout(box, Invoke(box, HostMethod(box, nameof(BudgetHost.Cpu4))));
            });

            Run("unity_frame_reset_reports_cpu_and_restores_allowance", () => {
                var box = NewBox();
                Require(Invoke(box, HostMethod(box, nameof(BudgetHost.Cpu2))).Exception == null,
                    "Initial 2 ms call should fit.");
                typeof(Cilbox.Cilbox).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(box, null);
                Require(box.usSpentLastFrame >= 2000 && box.usSpentLastFrame < 5000,
                    "Last-frame reporting should retain approximately 2 ms CPU.");
                Require(box.ExecutionBudget.CurrentFrameCpuNanoseconds == 0,
                    "Frame reset should zero the new frame's completed charge.");
                Require(Invoke(box, HostMethod(box, nameof(BudgetHost.Cpu4))).Exception == null,
                    "New frame did not receive a fresh 5 ms allowance.");
            });

            Run("ordinary_exception_unwinds_scope", () => {
                var box = NewBox(100_000);
                Invocation call = Invoke(box, HostMethod(box, nameof(BudgetHost.ThrowNow)));
                Require(call.Exception is InvalidOperationException &&
                    call.Exception.Message == "intentional host exception",
                    "Host exception should retain its identity.");
                Require(box.ExecutionBudget.ActiveDepth == 0, "Ordinary exception leaked active scope.");
            });

            Run("nested_child_timeout_disables_only_its_owner", () => {
                var parent = NewBox(100_000);
                var child = NewBox();
                CilboxMethod childMethod = HostMethod(child, nameof(BudgetHost.Cpu12));
                BudgetHost.NestedAction = () => childMethod.Interpret(null, Array.Empty<object>());
                try
                {
                    Invocation call = Invoke(parent, HostMethod(parent, nameof(BudgetHost.InvokeNested)));
                    Require(call.Exception is CilboxCpuBudgetTimeoutException timeout &&
                        ReferenceEquals(timeout.Owner, child), "Nested timeout lost its owning box.");
                    Require(child.disabled && !parent.disabled, "Nested child fault disabled the wrong owner.");
                    Require(parent.ExecutionBudget.ActiveDepth == 0 && child.ExecutionBudget.ActiveDepth == 0,
                        "Nested timeout leaked active accounting scopes.");
                }
                finally { BudgetHost.NestedAction = null; }
            });

            Run("actual_same_owner_callback_recursion_counts_cpu_once", () => {
                var owner = NewBox(100_000);
                owner.metadatas = new[] { HostToken(nameof(BudgetHost.InvokeNested)), HostToken(nameof(BudgetHost.Cpu2)) };
                CilboxMethod outer = Method(owner, "Outer", new byte[] { 0x28, 0, 0, 0, 0, 0x2a });
                CilboxMethod inner = Method(owner, "Inner", new byte[] { 0x28, 1, 0, 0, 0, 0x2a });
                BudgetHost.NestedAction = () => inner.Interpret(null, Array.Empty<object>());
                try
                {
                    for (int i = 0; i < 3; ++i)
                    {
                        long before = owner.ExecutionBudget.CurrentFrameCpuNanoseconds;
                        Invocation call = Invoke(owner, outer);
                        long charged = owner.ExecutionBudget.CurrentFrameCpuNanoseconds - before;
                        Require(call.Exception == null && owner.ExecutionBudget.ActiveDepth == 0,
                            "Same-owner nested callback did not return cleanly.");
                        Require(charged >= 2_000_000 && charged / 1e6 <= call.CpuMs + 0.2,
                            "Same-owner nested callback duplicated CPU.");
                    }
                }
                finally { BudgetHost.NestedAction = null; }
            });

            Run("actual_distinct_owner_callback_has_inclusive_cpu", () => {
                var parent = NewBox(100_000);
                var child = NewBox(100_000);
                CilboxMethod inner = HostMethod(child, nameof(BudgetHost.Cpu2));
                BudgetHost.NestedAction = () => inner.Interpret(null, Array.Empty<object>());
                try
                {
                    Invocation call = Invoke(parent, HostMethod(parent, nameof(BudgetHost.InvokeNested)));
                    Require(call.Exception == null, "Nested callback should fit both allowances.");
                    long parentCpu = parent.ExecutionBudget.CurrentFrameCpuNanoseconds;
                    long childCpu = child.ExecutionBudget.CurrentFrameCpuNanoseconds;
                    Require(childCpu >= 2_000_000 && parentCpu >= childCpu - 10_000,
                        "Parent did not include child CPU.");
                    Require(parent.ExecutionBudget.ActiveDepth == 0 && child.ExecutionBudget.ActiveDepth == 0,
                        "Nested callback leaked accounting scope.");
                }
                finally { BudgetHost.NestedAction = null; }
            });

            Run("ordinary_host_exception_is_catchable_by_script", () => {
                BudgetHost.Caught = 0;
                var owner = NewBox(100_000);
                Invocation call = Invoke(owner, CatchingHostMethod(owner, nameof(BudgetHost.ThrowNow)));
                Require(call.Exception == null && BudgetHost.Caught == 1 && !owner.disabled,
                    "Fixture must catch an ordinary exception before testing uncatchable budget faults.");
            });

            Run("nested_parent_timeout_is_uncatchable_and_disables_only_parent", () => {
                var parent = NewBox();
                var child = NewBox(100_000);
                CilboxMethod inner = HostMethod(child, nameof(BudgetHost.Cpu12));
                BudgetHost.NestedAction = () => inner.Interpret(null, Array.Empty<object>());
                BudgetHost.Caught = 0;
                int beforeCalls = BudgetHost.Calls;
                try
                {
                    Invocation call = Invoke(parent, CatchingHostMethod(parent, nameof(BudgetHost.InvokeNested)));
                    RequireTimeout(parent, call);
                    Require(call.Exception is CilboxCpuBudgetTimeoutException timeout &&
                        ReferenceEquals(timeout.Owner, parent), "Parent budget fault lost its owner.");
                    Require(!child.disabled && child.ExecutionBudget.ActiveDepth == 0,
                        "Parent budget fault disabled or leaked the child.");
                    Require(BudgetHost.Calls > beforeCalls && BudgetHost.Caught == 0,
                        "CPU workload did not execute or script catch swallowed the budget failure.");
                }
                finally { BudgetHost.NestedAction = null; }
            });

            Run("cross_box_virtual_ordinary_exception_caught_by_parent_leaves_child_usable", () => {
                var fixture = CrossBoxVirtualFixture(100_000, 100_000, nameof(BudgetHost.ThrowNow));
                BudgetHost.Caught = 0;
                Invocation call = Invoke(fixture.Parent, fixture.Caller, new object[] { fixture.Receiver });
                Require(call.Exception == null && BudgetHost.Caught == 1,
                    "Parent script should catch the child's ordinary virtual-call exception.");
                Require(!fixture.Parent.disabled && !fixture.Child.disabled,
                    "An ordinary caught cross-box exception disabled an owner.");
                fixture.Child.metadatas[0] = HostToken(nameof(BudgetHost.Cpu2));
                int calls = BudgetHost.Calls;
                Invocation second = Invoke(fixture.Parent, fixture.Caller, new object[] { fixture.Receiver });
                Require(second.Exception == null && BudgetHost.Calls == calls + 1,
                    "Child virtual dispatch was unusable after its caught ordinary exception.");
                Require(fixture.Parent.ExecutionBudget.ActiveDepth == 0 && fixture.Child.ExecutionBudget.ActiveDepth == 0,
                    "Cross-box ordinary exception leaked accounting scopes.");
            });

            Run("cross_box_virtual_child_budget_is_charged_and_uncatchable", () => {
                var fixture = CrossBoxVirtualFixture(100_000, 5000, nameof(BudgetHost.Cpu12));
                BudgetHost.Caught = 0;
                Invocation call = Invoke(fixture.Parent, fixture.Caller, new object[] { fixture.Receiver });
                Require(fixture.Receiver.disabled, "Over-budget virtual receiver must be disabled.");
                Require(fixture.Child.ExecutionBudget.ActiveDepth == 0, "Virtual child scope leaked.");
                Require(call.Exception is CilboxCpuBudgetTimeoutException timeout &&
                    ReferenceEquals(timeout.Owner, fixture.Child), "Virtual child timeout lost its owner.");
                Require(!fixture.Parent.disabled && BudgetHost.Caught == 0,
                    "Virtual child timeout disabled its parent or was caught by script.");
                Require(fixture.Parent.ExecutionBudget.ActiveDepth == 0, "Virtual parent scope leaked.");
            });

            Run("cross_box_virtual_parent_budget_is_inclusive_and_uncatchable", () => {
                var fixture = CrossBoxVirtualFixture(5000, 100_000, nameof(BudgetHost.Cpu12));
                BudgetHost.Caught = 0;
                Invocation call = Invoke(fixture.Parent, fixture.Caller, new object[] { fixture.Receiver });
                RequireTimeout(fixture.Parent, call);
                Require(call.Exception is CilboxCpuBudgetTimeoutException timeout &&
                    ReferenceEquals(timeout.Owner, fixture.Parent), "Virtual parent timeout lost its owner.");
                Require(!fixture.Child.disabled && !fixture.Receiver.disabled && BudgetHost.Caught == 0,
                    "Virtual parent timeout disabled its child or was caught by script.");
                Require(fixture.Child.ExecutionBudget.ActiveDepth == 0, "Virtual child scope leaked.");
            });
        }
#endif

        private static byte[] Workload(bool arithmetic)
        {
            byte[] body = new byte[WorkInstructions + 1];
            if (arithmetic)
                for (int i = 0; i < WorkInstructions; i += 4)
                { body[i] = 0x17; body[i + 1] = 0x18; body[i + 2] = 0x58; body[i + 3] = 0x26; }
            body[^1] = 0x2a;
            return body;
        }

        private static object Benchmark(string name, bool arithmetic)
        {
            var box = NewBox(2_000_000);
            CilboxMethod method = Method(box, name, Workload(arithmetic));
            var samples = new List<object>();
            for (int batch = -3; batch < 9; ++batch)
            {
                long wall0 = Stopwatch.GetTimestamp();
                long cpu0 = CpuMeasurement.ReadNanoseconds();
                for (int call = 0; call < BatchCalls; ++call)
                    method.Interpret(null, Array.Empty<object>());
                long cpu1 = CpuMeasurement.ReadNanoseconds();
                long wall1 = Stopwatch.GetTimestamp();
                Require(!box.disabled, "Generous-budget benchmark unexpectedly disabled the box.");
                if (batch >= 0) samples.Add(new { cpu_ms = (cpu1 - cpu0) / 1e6,
                    wall_ms = (wall1 - wall0) * 1000.0 / Stopwatch.Frequency,
                    instructions = (WorkInstructions + 1) * BatchCalls });
            }
            return new { name, warmup_batches = 3, measured_batches = 9,
                instructions_per_call = WorkInstructions + 1, calls_per_batch = BatchCalls,
                budget_us = 2_000_000, samples };
        }

        private static object BenchmarkShortReturns()
        {
            const int calls = 10_000;
            var box = NewBox(2_000_000);
            CilboxMethod method = Method(box, "ShortReturn", new byte[] { 0x2a });
            var samples = new List<object>();
            for (int batch = -3; batch < 9; ++batch)
            {
                long wall0 = Stopwatch.GetTimestamp();
                long cpu0 = CpuMeasurement.ReadNanoseconds();
                for (int call = 0; call < calls; ++call)
                    method.Interpret(null, Array.Empty<object>());
                long cpu1 = CpuMeasurement.ReadNanoseconds();
                long wall1 = Stopwatch.GetTimestamp();
                Require(!box.disabled, "Short-return benchmark unexpectedly disabled the box.");
                if (batch >= 0) samples.Add(new {
                    cpu_ms = (cpu1 - cpu0) / 1e6,
                    wall_ms = (wall1 - wall0) * 1000.0 / Stopwatch.Frequency,
                    cpu_ns_per_call = (cpu1 - cpu0) / (double)calls,
                    wall_ns_per_call = (wall1 - wall0) * 1e9 / Stopwatch.Frequency / calls,
                    instructions = calls
                });
            }
            return new {
                name = "short_return_callbacks", warmup_batches = 3, measured_batches = 9,
                instructions_per_call = 1, calls_per_batch = calls, budget_us = 2_000_000,
                samples
            };
        }

        private static object BenchmarkClockReads(bool cpuClock)
        {
            const int reads = 100_000;
            var samples = new List<object>();
            long checksum = 0;
            for (int batch = -3; batch < 9; ++batch)
            {
                long wall0 = Stopwatch.GetTimestamp();
                long cpu0 = CpuMeasurement.ReadNanoseconds();
                if (cpuClock)
                    for (int i = 0; i < reads; ++i) checksum ^= CpuMeasurement.ReadNanoseconds() + i;
                else
                    for (int i = 0; i < reads; ++i) checksum ^= Stopwatch.GetTimestamp() + i;
                long cpu1 = CpuMeasurement.ReadNanoseconds();
                long wall1 = Stopwatch.GetTimestamp();
                if (batch >= 0) samples.Add(new {
                    cpu_ms = (cpu1 - cpu0) / 1e6,
                    wall_ms = (wall1 - wall0) * 1000.0 / Stopwatch.Frequency,
                    cpu_ns_per_read = (cpu1 - cpu0) / (double)reads,
                    wall_ns_per_read = (wall1 - wall0) * 1e9 / Stopwatch.Frequency / reads
                });
            }
            return new {
                name = cpuClock ? "clock_thread_cpu" : "clock_stopwatch",
                warmup_batches = 3, measured_batches = 9, reads_per_batch = reads,
                measurement = "Whole loop/call cost; CPU wrapper dispatch and checksum included; no subtraction.",
                checksum = checksum.ToString("x"), samples
            };
        }

        public static int Main(string[] args)
        {
            TextWriter output = Console.Out;
            Console.SetOut(TextWriter.Null);
            try
            {
                Require(CpuMeasurement.IsCpuTime,
                    "Precise CPU provider unavailable; this run cannot validate CPU budgeting. " + CpuMeasurement.Name);
                CpuMeasurement.ReadNanoseconds();
                BudgetHost.Cpu2();
#if BASELINE
                const string mode = "baseline";
                Require(args.Contains("--benchmark-only"), "Baseline builds support --benchmark-only.");
#else
                const string mode = "implementation";
#endif
                if (args.Contains("--benchmark-only"))
                {
                    var benchmarks = new[] { Benchmark("finite_nop", false), Benchmark("finite_arithmetic", true),
                        BenchmarkShortReturns(), BenchmarkClockReads(false), BenchmarkClockReads(true) };
                    output.WriteLine(JsonSerializer.Serialize(new {
                        mode, scope = "Actual Cilbox in CoreCLR console with Unity stubs; NOT a Unity player.",
                        runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        cpu_clock = CpuMeasurement.Name, tiered_compilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                        benchmarks }, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
#if !BASELINE
                WarmRuntime();
                ActualInterpreterTests();
                AccountingTests.Run(Run);
                int failures = results.Count(x => !x.passed);
                output.WriteLine(JsonSerializer.Serialize(new {
                    mode, scope = "Actual Cilbox in CoreCLR console with Unity stubs; NOT a Unity player.",
                    runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    cpu_clock = CpuMeasurement.Name,
                    host_cpu_work = OperatingSystem.IsLinux() ? "synchronous native C" : "synchronous managed host",
                    passed = results.Count - failures, failed = failures, tests = results
                }, new JsonSerializerOptions { WriteIndented = true }));
                return failures == 0 ? 0 : 1;
#else
                return 2;
#endif
            }
            catch (Exception error)
            {
                output.WriteLine(JsonSerializer.Serialize(new { fatal = error.ToString() },
                    new JsonSerializerOptions { WriteIndented = true }));
                return 2;
            }
            finally { Console.SetOut(output); CpuMeasurement.Dispose(); }
        }
    }
}
