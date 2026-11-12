using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Cilbox
{
	// One ledger per Cilbox. Absolute CPU timestamps never cross OS threads.
	internal sealed class CilboxExecutionBudget
	{
		internal sealed class ThreadState
		{
			// Only the executing thread writes this hot-path counter.
			internal long Instructions;
			internal readonly List<Activation> Active = new List<Activation>();
			internal readonly List<Activation> Scopes = new List<Activation>();
		}

		internal sealed class Activation
		{
			internal CilboxExecutionBudget Budget;
			internal ThreadState Thread;
			internal ICilboxThreadCpuClock Clock;
			internal CilboxMethod Method;
			internal int Depth;
			internal long LastCpu;
			internal long SampledWall;
			internal long LastInstructions;
			internal long CallCpu;
			// Only this activation's executing thread writes or reads these caches.
			// Foreign frame snapshots invalidate them through Budget.version.
			internal bool GateCanSkip;
			internal long GateWallStart;
			internal long GateWallDelay;
			internal long GateLimitUs;
			internal int GateVersion;
		}

		[ThreadStatic] private static ThreadState currentThread;
		internal static ThreadState CurrentThreadState =>
			currentThread ?? (currentThread = new ThreadState());

		// Tests replace these only while all scopes are idle.
		internal static Func<ICilboxThreadCpuClock> ClockFactory = CilboxThreadCpuClock.OpenCurrentThread;
		internal static Func<long> WallTimestampProvider = Stopwatch.GetTimestamp;
		internal static long WallFrequency = Stopwatch.Frequency;

		private readonly Cilbox owner;
		private readonly object sync = new object();
		private readonly List<Activation> active = new List<Activation>();
		private long usedNanoseconds;
		private long instructions;
		private int depth;
		private int version;
		private bool exhausted;
		private long exhaustedNanoseconds;
		private long exhaustedLimitNanoseconds;
		private string clockFailure;
		private string clockDescription = "Not sampled";
		private string cpuClockDescription;
		private string wallClockDescription;

		internal CilboxExecutionBudget(Cilbox owner)
		{
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
		}

		internal long CurrentFrameCpuNanoseconds { get { lock (sync) return usedNanoseconds; } }
		internal int ActiveDepth { get { lock (sync) return depth; } }
		internal string ClockDescription { get { lock (sync) return clockDescription; } }

		internal void Enter(CilboxMethod method)
		{
			ThreadState thread = CurrentThreadState;
			if (thread.Active.Count == 0) thread.Instructions = 0;
			Exception pending = SettleThread(thread, method, 0);
			if (pending != null) throw pending;

			Activation entry = null;
			lock (sync)
			{
				BeginUpdateLocked();
				// Admission includes CPU already consumed by concurrent activations.
				for (int i = 0; i < active.Count; ++i) SampleLocked(active[i]);
				LatchLimitLocked();
				pending = FailureLocked(method, 0);
				if (pending == null)
				{
					for (int i = 0; i < active.Count; ++i)
						if (ReferenceEquals(active[i].Thread, thread)) { entry = active[i]; break; }

					if (entry == null)
					{
						ICilboxThreadCpuClock clock = ClockFactory();
						try
						{
							long wall = WallTimestampProvider();
							if (!clock.TryReadNanoseconds(out long cpu) || cpu < 0)
								throw new InvalidOperationException("Unable to initialize Cilbox execution clock: " + clock.Name);
							entry = new Activation
							{
								Budget = this, Thread = thread, Clock = clock, Method = method,
								LastCpu = cpu, LastInstructions = thread.Instructions, SampledWall = wall
							};
							active.Add(entry);
							ObserveClockLocked(clock);
						}
						catch
						{
							clock.Dispose();
							throw;
						}
					}
					++entry.Depth;
					++depth;
				}
				RefreshGatesLocked();
				MirrorLocked();
			}
			if (pending != null) throw pending;
			if (entry.Depth == 1) thread.Active.Add(entry);
			thread.Scopes.Add(entry);
		}

		// Used at the 64-instruction checkpoint and immediately after native/shim calls.
		// Wall time is only an inexpensive upper-bound gate; it never proves a CPU timeout.
		internal static void Checkpoint(CilboxMethod method, int pc)
		{
			Checkpoint(currentThread, method, pc);
		}

		// InterpretInner already caches this state. Avoid another TLS lookup at
		// every 64-instruction checkpoint (cost varies substantially by runtime).
		internal static void Checkpoint(ThreadState thread, CilboxMethod method, int pc)
		{
			if (thread == null || thread.Active.Count == 0) return;
			long now = WallTimestampProvider();
			for (int i = 0; i < thread.Active.Count; ++i)
			{
				Activation entry = thread.Active[i];
				CilboxExecutionBudget budget = entry.Budget;
				long elapsed = unchecked(now - entry.GateWallStart);
				if (entry.GateCanSkip &&
					entry.GateVersion == Volatile.Read(ref budget.version) &&
					entry.GateLimitUs == budget.owner.timeoutLengthUs &&
					elapsed >= 0 && elapsed < entry.GateWallDelay)
					continue;

				Exception failure;
				lock (budget.sync)
				{
					budget.BeginUpdateLocked();
					// Sample after locking: a foreign-thread frame snapshot may have
					// advanced LastCpu while this thread was waiting for the lock.
					// Include unpublished CPU in synchronous native work on sibling threads.
					for (int j = 0; j < budget.active.Count; ++j) budget.SampleLocked(budget.active[j]);
					budget.LatchLimitLocked();
					budget.RefreshGatesLocked();
					budget.MirrorLocked();
					failure = budget.FailureLocked(method, pc);
				}
				if (failure != null) throw failure;
			}
		}

		// Always removes the matching scope. On exception unwind, enforce=false
		// records/latches any overrun without replacing the original exception.
		internal long Exit(bool enforce)
		{
			ThreadState thread = currentThread;
			if (thread == null || thread.Scopes.Count == 0)
				throw new InvalidOperationException("Unbalanced Cilbox execution-budget exit.");
			Activation entry = thread.Scopes[thread.Scopes.Count - 1];
			if (!ReferenceEquals(entry.Budget, this))
				throw new InvalidOperationException("Cilbox execution-budget scopes must exit in stack order.");

			Exception pending = null;
			long elapsed = 0;
			bool dispose = false;
			try
			{
				pending = SettleThread(thread, entry.Method, 0);
			}
			finally
			{
				lock (sync)
				{
					BeginUpdateLocked();
					--entry.Depth;
					--depth;
					if (entry.Depth == 0)
					{
						elapsed = entry.CallCpu;
						active.Remove(entry);
						dispose = true;
					}
					RefreshGatesLocked();
					MirrorLocked();
				}
				thread.Scopes.RemoveAt(thread.Scopes.Count - 1);
				if (dispose)
				{
					thread.Active.Remove(entry);
					try { entry.Clock.Dispose(); }
					catch (Exception error)
					{
						if (pending == null) pending = new InvalidOperationException(
							"Unable to close Cilbox execution clock: " + entry.Clock.Name, error);
					}
				}
			}
			if (enforce && pending != null) throw pending;
			return elapsed;
		}

		// The bound readers permit snapshots of active worker threads. Each interval
		// is settled once before the old frame is published and its ledger is reset.
		internal long ResetFrame()
		{
			lock (sync)
			{
				BeginUpdateLocked();
				for (int i = 0; i < active.Count; ++i) SampleLocked(active[i]);
				LatchLimitLocked();
				long completed = usedNanoseconds;
				usedNanoseconds = 0;
				instructions = 0;
				// An active overrun must survive rotation until its executing scope
				// observes it. Idle, non-disabled boxes may receive a fresh next frame.
				if (depth == 0 && !owner.disabled)
				{
					exhausted = false;
					exhaustedNanoseconds = exhaustedLimitNanoseconds = 0;
				}
				RefreshGatesLocked();
				MirrorLocked();
				return completed;
			}
		}

		private static Exception SettleThread(ThreadState thread, CilboxMethod method, int pc)
		{
			Exception pending = null;
			// One activation per DISTINCT box on this thread: A -> B -> A charges
			// A once and B once. These are inclusive budgets, not additive CPU totals.
			for (int i = 0; i < thread.Active.Count; ++i)
			{
				Activation entry = thread.Active[i];
				CilboxExecutionBudget budget = entry.Budget;
				lock (budget.sync)
				{
					budget.BeginUpdateLocked();
					for (int j = 0; j < budget.active.Count; ++j) budget.SampleLocked(budget.active[j]);
					budget.LatchLimitLocked();
					budget.RefreshGatesLocked();
					budget.MirrorLocked();
					if (pending == null) pending = budget.FailureLocked(method, pc);
				}
			}
			return pending;
		}

		// Must be called under this box's lock, including on the frame thread.
		private void SampleLocked(Activation entry)
		{
			if (clockFailure != null) return;
			try
			{
				long wall = WallTimestampProvider();
				if (!entry.Clock.TryReadNanoseconds(out long cpu) || cpu < entry.LastCpu)
				{
					clockFailure = "Cilbox execution clock failed or moved backwards: " + entry.Clock.Name;
					return;
				}
				long delta = cpu - entry.LastCpu;
				entry.LastCpu = cpu;
				entry.SampledWall = wall;
				entry.CallCpu = SaturatingAdd(entry.CallCpu, delta);
				usedNanoseconds = SaturatingAdd(usedNanoseconds, delta);
				long currentInstructions = Volatile.Read(ref entry.Thread.Instructions);
				long instructionDelta = currentInstructions - entry.LastInstructions;
				if (instructionDelta > 0) instructions = SaturatingAdd(instructions, instructionDelta);
				entry.LastInstructions = currentInstructions;
			}
			catch (Exception error)
			{
				// Never switch from an established CPU-clock origin to wall time.
				clockFailure = "Cilbox execution clock failed (" + entry.Clock.Name + "): " + error.Message;
			}
		}

		private void LatchLimitLocked()
		{
			long limit = BudgetNanoseconds(owner.timeoutLengthUs);
			if (!exhausted && (limit == 0 || usedNanoseconds > limit))
			{
				exhaustedNanoseconds = usedNanoseconds;
				exhaustedLimitNanoseconds = limit;
				exhausted = true;
			}
		}

		private Exception FailureLocked(CilboxMethod method, int pc)
		{
			if (clockFailure != null) return new InvalidOperationException(clockFailure);
			if (!exhausted) return null;
			CilboxMethod attributedMethod = method;
			int attributedPc = pc;
			if (method == null || method.parentClass == null || !ReferenceEquals(method.parentClass.box, owner))
			{
				attributedMethod = active.Count == 0 ? null : active[0].Method;
				attributedPc = 0; // The current instruction belongs to another box.
			}
			return new CilboxCpuBudgetTimeoutException(owner, attributedMethod, attributedPc,
				exhaustedNanoseconds, exhaustedLimitNanoseconds, clockDescription);
		}

		private void RefreshGatesLocked()
		{
			long limitUs = owner.timeoutLengthUs;
			long limit = BudgetNanoseconds(limitUs);
			long remaining = usedNanoseconds >= limit ? 0 : limit - usedNanoseconds;
			// A small conservative margin accommodates ordinary relative clock-rate
			// and rounding error. Unsupported clocks are diagnosed by their provider.
			long conservative = remaining - remaining / 100 - Math.Min(remaining, 2000);
			if (conservative < 0) conservative = 0;
			double delayTicks = Math.Floor(conservative * (WallFrequency / 1_000_000_000.0));
			long delay = delayTicks >= long.MaxValue ? long.MaxValue : delayTicks <= 0 ? 0 : (long)delayTicks;
			int publishedVersion = unchecked((version | 1) + 1);
			for (int i = 0; i < active.Count; ++i)
			{
				Activation entry = active[i];
				// A foreign CPU snapshot changes the authoritative baseline, but must
				// not mutate another thread's hot-path cache. Its version will mismatch.
				if (!ReferenceEquals(entry.Thread, currentThread)) continue;
				entry.GateWallStart = entry.SampledWall;
				entry.GateWallDelay = delay;
				entry.GateLimitUs = limitUs;
				entry.GateCanSkip = !exhausted && clockFailure == null && active.Count == 1;
				entry.GateVersion = publishedVersion;
			}
			Volatile.Write(ref version, publishedVersion);
		}

		private void BeginUpdateLocked()
		{
			// Invalidate thread-owned gate caches before changing the ledger. The
			// full fence also orders subsequent payload writes after this odd marker.
			// Publish the next even version only after the update is complete.
			Interlocked.Exchange(ref version, unchecked((version + 1) | 1));
		}

		private void ObserveClockLocked(ICilboxThreadCpuClock clock)
		{
			if (clock.IsCpuTime) cpuClockDescription = clock.Name;
			else wallClockDescription = clock.Name;
			// Retain a mixed-mode diagnostic across frames: an earlier frame report
			// must not be relabeled pure CPU after a transient fallback recovers.
			clockDescription = cpuClockDescription != null && wallClockDescription != null
				? "Mixed execution clocks since initialization: " + cpuClockDescription + "; " + wallClockDescription
				: cpuClockDescription ?? wallClockDescription;
		}

		private void MirrorLocked()
		{
			owner.interpreterAccountingDepth = (uint)depth;
			owner.interpreterAccountingCumulitiveTicks = usedNanoseconds;
			owner.interpreterInstructionsCount = instructions;
			owner.interpreterTicksInUs = 1000; // Compatibility diagnostics now use nanoseconds.
			owner.interpreterAccountingDropDead = 0; // No shared absolute thread-CPU deadline exists.
		}

		private static long BudgetNanoseconds(long microseconds)
		{
			if (microseconds <= 0) return 0;
			return microseconds > long.MaxValue / 1000 ? long.MaxValue : microseconds * 1000;
		}

		private static long SaturatingAdd(long value, long delta)
		{
			return delta > long.MaxValue - value ? long.MaxValue : value + delta;
		}
	}

	internal sealed class CilboxCpuBudgetTimeoutException : CilboxInterpreterTimeoutException
	{
		internal Cilbox Owner { get; }
		internal CilboxCpuBudgetTimeoutException(Cilbox owner, CilboxMethod method, int pc,
			long chargedNanoseconds, long limitNanoseconds, string clock)
			: base("Script execution budget exceeded (" + chargedNanoseconds / 1000 + "/" +
				limitNanoseconds / 1000 + " us; " + clock + ")",
				method?.parentClass?.className ?? "<Cilbox>",
				method?.methodName ?? "<entry>", pc)
		{
			Owner = owner;
		}
	}
}
