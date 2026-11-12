# Cilbox

<IMG SRC=https://github.com/cnlohr/cilbox/actions/workflows/build.yml/badge.svg>

`Cilbox` is a CIL emulator, geared for Unity. It allows the execution of arbitrary C# code (more specifically: CIL bytecode) in a relatively sandboxed manner. In that whatever access is granted to types/methods, the script running within the sandbox can access.  This is NOT ia JITter.  This does not use fancy language constructs, and works (actually works best) in IL2CPP.

The performance is surprisingly high for something that is written in C# and not JITting.

Performance has been tested on Synergiance's 6502 emulator.  It is able to just barely hit `2MHz` in-system. While running the 6502, verses about `240MHz` native, the rate that this system executes CIL insturctions is about `168 MHz`, when targeting IL2CPP in Unity 6.1.2f1 running on a 9950X3D.

Performance testing between versions of Cilbox is possible with this command:

```sh
dotnet run --project TestCilbox/TestCilbox.csproj -c Release -- --perf
```

## Security Disclaimer

I am not a security researcher. I cannot make promises about the security of the core portion of the engine. I also cannot make claims to the fitness or suitability for any purpose.  There are likely bugs, so if you have one, I strongly encourage submitting a pull request.

## Design

There is a `Cilbox` object that gets placed in the scene at runtime. This box
is responsible for loading and manging the various `CilboxProxy` classes that
are converted.  There can be multiple cilboxes one per scene loaded in a world.
Ideally the scene could have a `CilboxScene` and an avatar could also have a `CilboxAvatar`

![Build Process](Docs/buildprocess.svg)

![Run Process](Docs/runprocess.svg)

### The general approach
1. You mark your class as `[Cilboxable]`
2. On build, any components with a `[Cilboxable]` script are serialized, and replaced with a `CilboxProxy` which has a public String containing the serialized data for its serialized fields and referenced objects.
3. All classes that are `[Cilboxable]` are reflected and all fields information and method bytecode is extracted and saved off as a serialized ball of goo.
4. All `[Cilbox`ed classes are loaded out of the ball of goo into a dictionary of classes where the bytecode is kept as byte arrays, and static members are configured with their appropriate types.
5. When running, each gameobject with a `CilboxProxy` will wake up, and load into a series of `object`s the data that was part of the original class.
6. Whenever a forwarded Unity entrypoint is called, the `CilboxProxy` will ask `Cilbox` to emulate the bytecode associated with that method.
7. The bytecode emulator can make all needed decisions about proper sandboxing, infinte loop termination, etc. But for the most part, my intent is to just disable any code that's doing something that could be usnafeish.

### Cilbox has
 * `CilboxableAttribute` So you can add `[Cilboxable]` to your class as an attribute that will tell Cilbox to emulate it.
 * `CilboxProxy` - The MonoBehaviour that replaces your script.
 * `Cilbox` - Internal, abstract class that contains a given asset's "Box" that its scripts live in.
 * `CilboxAvatar` / `CilboxScene` - Concrete classes from `Cilbox` that is added to each Avatar or Scene, respectively.

### Cilbox internally uses
 * `CilboxMethod` - for holding information about classes that are being overridden.
 * `CilboxClass` - for holding information about classes that are being overridden.
 * `StackElement` - a generic "object" like thing that can be written into/altered/etc, without needing to box/unbox/etc.  This holds parameters, locals, and the stack.
 * These Stack Elements can be: `Boolean`, `Sbyte`, `Byte`, `Short`, `Ushort`, `Int`, `Uint`, `Long`, `Ulong`, `Float`, `Double`, `Object`, `Address`, and `Reference`
 * If the StackElement is an `Object`, boxing will need to happen when it gets used.
 * If the StackElement is an `Array`, then it is actually a reference, where .o contains the link to the `Array` and .i contains the reference to the element.

### Security

The general idea is that at load, the Cilbox will load all classes, metadata, and methods.  Then any references that script has to the surrounding system are patched in, but only if the specific class/method is allowed.

In general, the process for deciding if a feature is allowed is:
1. Is the native type pertaining to this meta (callee, parameter, field, etc.) allowed by calling `CheckTypeAllowed( String sType )`.
2. If the access is a method call, `CheckMethodAllowed()` is consulted before the reference is made.
3. If the access is a native field reference, `CheckFieldAllowed()` is consulted before the reference is made.
4. Component access can also be remapped through `GetComponentTypeOverride()` for shim / replacement scenarios.

## Security model
 * Access to native types, methods, and fields is allowlist-based. Supported compound types currently include arrays (including object arrays), enums, internal structs / value types, nested generic types, and tested `Dictionary<,>` usage, but only when the required native surface is explicitly permitted.
 * Access to fields on objects outside the cilbox is also allowlist-based. Properties remain supported, and explicitly approved native fields are supported too. Arbitrary external field access is still blocked.
 * General-purpose reflection should still be considered unsupported. The runtime uses reflection internally for whitelisting and dispatch, but exposing open reflection to guest code would be difficult to secure.

For additional information:
 * For all security-related notes surrounding the load decisions, please see [CilboxUsage.cs](Packages/com.cnlohr.cilbox/CilboxUsage.cs). 
 * For avatar/scene decisions regarding what can or cannot be loaded, please see the specific [CilboxAvatar.cs](Packages/com.cnlohr.cilbox/CilboxAvatar.cs) / [CilboxScene.cs](Packages/com.cnlohr.cilbox/CilboxScene.cs). 

### Resource accounting

`timeoutLengthUs` is a cumulative allowance per box between its existing Unity `Update()` frame resets. The default remains 500 ms, and avatars retain their 5 ms limit.

Every 64 interpreted instructions, the interpreter checks a cheap Stopwatch guard. When that guard expires, it reads the executing thread's CPU clock and throws a timeout only if the accumulated CPU allowance has actually been exceeded. A wait or descheduling pause can expire the guard without exhausting the allowance; the next guard then uses the CPU time still available. CPU already consumed earlier in the frame is retained.

Calls also settle CPU at entry and exit. Native/shim returns check the guard immediately, so a CPU-heavy native call followed by `ret`, or several short calls adding up to the limit, cannot avoid enforcement. A synchronous native call cannot be interrupted while it is running; its overrun is detected when control returns.

Accounting includes interpreter work and synchronous native work, including Unity or GC work that runs on the active thread. Sleeping, blocking and time when that thread is descheduled do not count with a supported CPU clock. Work on unrelated Unity/GC worker threads is not charged to the caller's thread budget.

Recursion and reentry into the same box count each CPU interval once. Concurrent threads contribute the sum of their CPU usage to the shared box budget. Nested calls between different boxes use inclusive attribution: the caller and callee each include that call, so their reports must not be added together as a process-wide CPU total. This accounting does not make Unity APIs or other interpreter data structures safe for arbitrary concurrent use.

A frame reset samples all active threads before publishing the completed frame and rebasing their CPU baselines. An overrun already observed in an active call remains latched across that reset until the call unwinds.

#### Clock support and reporting

| Runtime platform | CPU clock |
| --- | --- |
| Windows x64, including Unity Mono, IL2CPP and CoreCLR | `QueryThreadCycleTime`, converted using the bundled native helper's locally calibrated invariant-TSC frequency, after platform and clock qualification. |
| 64-bit Linux and 64-bit Android | A bound POSIX thread CPU clock from `pthread_getcpuclockid` and `clock_gettime`. |
| Other platforms, missing native helper, or failed startup qualification | Explicit Stopwatch fallback, with a warning and a diagnostic clock description. Pauses can still count in this mode. |

The Windows helper is supplied as a Windows x64 Unity plugin alongside its [source and reproducible build instructions](Packages/com.cnlohr.cilbox/Native~/README.md). It does not use coarse `GetThreadTimes`, an advertised CPU GHz value, or `Stopwatch.Frequency` to convert thread cycles. Its startup qualification is performed before scene loading in Unity. See the native documentation for the limits of this conversion and the validation still required on target players.

`executionTimeClock` identifies the selected clock or fallback and retains a mixed-mode diagnostic if the box has used both. `usSpentLastFrame` and function profiling report CPU microseconds when a CPU clock is active. The legacy `interpreterAccountingCumulitiveTicks` diagnostic now uses nanoseconds (`interpreterTicksInUs == 1000`); the legacy `interpreterAccountingDropDead` is retained as zero because guard deadlines are private to each thread. These diagnostic fields no longer control enforcement.

The existing proxy/box disable behavior applies to the owner of an exceeded budget. Timeout exceptions cannot be swallowed by interpreted catch blocks, including after a native callback wraps one in `TargetInvocationException`.

Run the dedicated accounting and interpreter regressions with:

```sh
dotnet run --project TestCilboxCpuBudget/TestCilboxCpuBudget.csproj -c Release
```

The [test harness documentation](TestCilboxCpuBudget/README.md) covers alternate CoreCLR versions, clock measurements, and comparison with the previous Stopwatch implementation.

### Current limitations
 * You still cannot arbitrarily expose a custom method directly to Unity UI / inspector event pickers. Invocation is limited to the entrypoints forwarded by `CilboxProxy`, such as `Awake()`, `Start()`, `FixedUpdate()`, `Update()`, `OnEnable()`, `OnDisable()`, `OnDestroy()`, `OnTriggerEnter/Exit()` and `OnCollisionEnter/Exit()`.

## Cleanup
 * Clean up the `GetConstructors` code to use `GetConstructor` but we need access to the modifiers.
 * Improve the definitions for serializing the list of data for Methods, Fields, and Strings in the `assemblyRoot["metadata"]`. Maybe encapsulate again.  
 * Make it so we can initialize before Start.  Waiting til Start is very depressing.
 * Try to make sense of when we should be init/start/awakening'ing.
 * Figure out where "The referenced script (Unknown) on this Behaviour is missing!" is coming from.



