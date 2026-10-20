# Cilbox quirks verification — 2026-10-20

Source report: https://gist.github.com/hai-vr/85ebd787768b836d557a9b9acc0fa071

## Revision and harness

- Compared directly against `cnlohr/cilbox` `origin/master` at `d7f0c8d934dc2721362c4c1690b33aa10acb955f` (2026-09-30).
- The original local `fix-nullable-support` checkout and its untracked `REVIEW_HANDOFF.md` were not modified.
- .NET SDK: 10.0.113.
- Baseline: `dotnet run --project TestCilbox/TestCilbox.csproj -c Release` passes.
- Probes: `dotnet run --project TestCilbox/TestCilbox.csproj -c Release -- --gist-quirks` (intentionally returns nonzero while regressions remain).
- Separate metadata probes: `--gist-typeof` and `--gist-static-box` (each in its own process; the latter currently throws during initialization).
- All probes run C# compiled into the existing TestCilbox simulator, then interpreted by Cilbox. Native C# output is used as the expected behavior. Probes are opt-in so the ordinary suite still passes.

## Verified against current upstream

| Gist claim / test | Result | Evidence |
| --- | --- | --- |
| `ldobj` (0x71) missing | **Reproduced** | Struct array compound assignment throws `Opcode 0x71 unimplemented`, PC 37 |
| `conv.r.un` (0x76) missing | **Reproduced** | `uint * float` throws `Opcode 0x76 unimplemented`, PC 2 |
| `bool[]` broken | **Reproduced** | Array write/read raises `ArgumentException: Cannot widen from source type to target type` |
| Struct local not initialized | **Reproduced** | `Vector2 result; result.X = ...` raises field-on-`Boolean` `ArgumentException` |
| Struct array field mutations lost | **Reproduced** | `positions[0].X = 9` returns original `<1, 2>` rather than `<9, 2>` |
| `byte[][]` fails type checking | **Not reproduced with tested forms** | Local `byte[][]` and a serialized public `byte[][]` field both round-trip successfully (42 and 33, respectively) |
| `castclass` null handling | **Reproduced** | `(string)(object)null` throws CilboxInterpreterRuntimeException instead of producing null |
| `isinst` assignability | **Reproduced** | `string` as `IComparable` (allowed in test whitelist) returns false instead of true |
| `typeof(BoxType)` unsupported | **Reproduced, symptom differs** | The `InspectBoxType` method is not present in the interpreted method lookup (`KeyNotFoundException`); no successful `typeof` |
| Statics whose type is defined inside the box | **Reproduced** | A static `GistStaticBoxFieldBehaviour` field causes `NullReferenceException` in `DeserializeDataForProxyField` during `BoxInitialize` |
| Uninitialized private primitive field | **Passes** | The private `int` defaults to `0`, consistent with the report's note about newer versions |
| Overloaded interpreted methods | **Existing tests pass** | `OverloadEcho(int)` and `OverloadEcho(string)` both pass in the existing suite; not a proof all overloads work |
| Exception isolation / disabled proxies | **Existing tests pass** | Faulted proxy is disabled, sibling and survivor continue, and overall box remains enabled |
| Execution budget | **Existing tests pass** | Timeout test enforces a configured 200 ms budget and disables proxy on timeout; this does not separately validate the default 500 ms on every Basis host |
| `GetComponent<T>()` | **Existing test passes** | `GetComponent Polymorphic Tag = 100`; clone and Basis-specific paths not covered |

## Not fully verified in standalone harness

The following need integration testing against the Basis client and actual scene/avatar loading: client vs Editor API allowlist, content-police replacement of Unity constraints with Basis constraints, denied/unsupported components on instantiated clones, proxy `Start` ordering and inactive objects, clone loading and serialized data lifetime, same-frame calls on newly instantiated proxies, GameObject vs Component `GetComponent` behavior in clones, Unity delegate/event forwarding, EditorOnly destroyed components, and VRWare timer/input behavior.

The report's statements about abstract class bodies, static class annotations, Unity filenames, overload resolution beyond the cases above, Unity `Vector2.op_Implicit` lookup, and exact guest exception-stack presentation were not individually reproduced here.

## Implementation notes

- The interpreter opcode switch in `Packages/com.cnlohr.cilbox/Cilbox.cs` has `stobj` (0x81) but no cases for 0x71 or 0x76.
- `castclass` and `isinst` currently require exact native type equality and `castclass` throws when the resulting reference is null.
- `bool[]` uses the primitive-array interpreter path, which uses incompatible `sbyte[]`/`byte[]` casts or primitive assignments.
- For a follow-up fix, retain these probes as regression tests and run the original suite after each opcode/type-system change.
- This is a verification-only branch: **no Cilbox runtime fixes or Basis vendored-package update have been made**.
