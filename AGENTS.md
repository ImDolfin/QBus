# Agent guide

This file applies to the whole repository. Follow current user instructions when they change the task or an established requirement. Keep this guide aligned with the implementation; it describes the current design, not a requirement to preserve an obsolete design after an authorized change.

## Start here

- Read [README.md](README.md) for the overview and development commands, and [the API guide](Packages/com.eventbus.queued/Documentation~/guide.md) for observable behavior.
- Check `git status --short` before editing. Preserve unrelated user changes and work from other agents.
- Identify the affected layer before changing code. Assign separate files or directories to collaborating agents and coordinate any shared-file edits.
- Use [the validation record](Packages/com.eventbus.queued/Documentation~/validation.md) as dated evidence, not proof that a new change passes.

## Directory boundaries

| Location | Responsibility and boundary |
| --- | --- |
| `Packages/com.eventbus.queued/` | The installable UPM package. Consumers must not need repository tooling or a .NET SDK to use it in Unity. |
| `Packages/com.eventbus.queued/Runtime/Core/` | Shared contracts, publication context, typed queues, subscriptions, routing catalog, and owned `EventBus`. Keep the core usable outside Unity. Unity-specific logging is conditionally compiled; avoid introducing unconditional Unity dependencies. |
| `Packages/com.eventbus.queued/Runtime/Unity/` | Static `Events` facade, PlayerLoop integration, and Play-session lifecycle. Keep this separate from the manually owned core bus. |
| `Packages/com.eventbus.queued/Runtime/Analyzers/` | Bundled generator DLL and its carefully configured importer metadata. Edit generator source under `Tools`, then rebuild this artifact. Do not bundle Roslyn dependency DLLs. |
| `Packages/com.eventbus.queued/Editor/` | Editor lifecycle integration. Keep `UnityEditor` dependencies out of runtime assemblies. |
| `Packages/com.eventbus.queued/Tests/` | Unity EditMode/PlayMode tests and separate event/fixture assemblies. Preserve cross-assembly coverage; merging fixtures into one assembly would weaken it. |
| `Packages/com.eventbus.queued/Samples~/` | Importable examples. Keep them usable with only the installed package and their declared Unity dependencies. |
| `Packages/com.eventbus.queued/Documentation~/` | Package documentation and shared visual assets. Preserve the `~` folder names. The overview SVG is embedded in both READMEs. |
| `Tools/QueuedEventBus.Core/` | Standalone build project for the existing package core sources. It links those sources; do not create a second runtime implementation here. |
| `Tools/QueuedEventBus.Generator/` | Authoritative source for the Roslyn generator and usage analyzer. See its [development guide](Tools/QueuedEventBus.Generator/README.md). |
| `Tools/QueuedEventBus.Generator.Tests/` | Executable harness for generated-source compilation, diagnostics, registration, and cross-assembly routing. |
| `Tools/Update-PackageMetadata.ps1` | Creates missing deterministic Unity `.meta` files while preserving existing metadata. |
| `Tests/QueuedEventBus.Tests/` | Standalone executable correctness/allocation harness and throughput benchmarks. These are run with `dotnet run`, not `dotnet test`. |
| `Tests/run-tests.ps1` | Common entry point for .NET, Unity, benchmark, and IL2CPP validation. |
| `TestProject/` | Minimal Unity validation host referencing the local package. Production behavior belongs in the package, not host scripts or project settings. |
| `.github/workflows/` | CI configuration. Current CI runs the .NET core and generator harnesses; it does not validate Unity or IL2CPP. |
| `artifacts/`, `bin/`, `obj/`, Unity caches | Disposable outputs, reports, previews, and experiments. Never make package behavior or maintained validation depend on scratch files here. Do not commit these outputs. |

## Compatibility and source generation

- Minimum supported Unity version is **2022.3**. Package/runtime and generator sources use C# 9-compatible syntax; development harnesses may use newer language features. Keep Unity's compiler and available APIs in mind when developing with a newer desktop SDK.
- The standalone core project targets **.NET Standard 2.1**. The generator targets **.NET Standard 2.0 with Roslyn 3.8**. Preserve these compatibility boundaries unless the task explicitly changes them.
- Maintained standalone harnesses and CI currently target **.NET 9**. The core has also passed an isolated .NET 8.0.21 correctness/allocation run, but that does not add a maintained .NET 8 test target.
- Standalone .NET use is not automatically initialized today: `Events` is Unity-specific, and the generator's startup hooks use Unity attributes. Outside Unity, callers need explicit route registration and an owned, manually pumped `EventBus`. Do not claim automatic standalone integration until it exists and is tested.
- Events implement `IEvent`; keep support for immutable structs and class payloads. Do not introduce a mandatory event base class without an explicit design change.
- The generator discovers supported concrete event declarations in each source assembly. Events need neither attributes nor `partial`. Generic events, ref-like events, and inaccessible declarations are unsupported. Discovery inside third-party DLLs without generated metadata is outside the current design.
- Generated registration includes the concrete type and every distinct event base/interface, including `IEvent`. Preserve diamond deduplication, concrete generic adapters, editor registration, and linker-preservation attributes.
- Preserve actionable diagnostics: QEB001 for unsupported declarations, QEB002 for calls requiring concrete types, and QEB003 for statically detectable erased publication types. Keep runtime validation for cases static analysis cannot prove.
- Do not edit emitted `QueuedEventBus.Registration.g.cs` or generated compiler outputs. Change the generator and test the resulting source.

## Runtime invariants

These are observable behavior and performance constraints, not incidental implementation details:

1. **Thread ownership:** an owned bus belongs to its constructing thread. The Unity facade is used on Unity's main thread. Premature facade access must fail before changing state; supported use starts at scene `Awake`.
2. **Typed queues:** one retained ring buffer per used concrete type, starting at 16 entries and growing geometrically. Categories are subscription groups over those queues, not additional payload queues. Prewarming also prepares scheduler capacity.
3. **Exact publication types:** reject null class payloads and derived instances raised under a concrete base type. Referenced payload data is not cloned and must remain stable while queued.
4. **Per-publication context:** store payload and immutable `EventContext` together. Capture the timestamp during `Raise`; never stamp a reused payload object. Sequence starts at one per bus, spans all types, excludes rejected raises, and throws on exhaustion rather than wrapping. It identifies publication order, not cross-channel delivery order. Timestamp units are `Stopwatch` ticks, not UTC or Unity scaled time.
5. **Pump snapshot:** capture pending counts for ALL active channels before invoking any callback. Drain those counts completely in channel activation order, preserving FIFO within each channel. Any handler-raised event waits for the next pump, including events sent to a later or newly created channel.
6. **Subscriber snapshot:** select subscribers at delivery time and capture the immutable delivery array once per event. Subscription changes affect the next event, which may be in the current pump. The current event finishes its captured callbacks.
7. **Subscription identity:** exact and category callbacks, with and without context, share registration order. Separate registrations mean separate deliveries. Copied/default/disposed tokens must remain idempotent.
8. **Resilience and lifetimes:** contain each listener exception and reporter failure, then continue. Reject recursive pumping and bus disposal during dispatch. Clear dequeued entries and release queues/subscriptions on disposal. Callback-induced queue growth must not invalidate the current payload or context references.
9. **Warm-path allocations:** publication and delivery must allocate no bus memory after capacity and subscriptions are prepared, including struct delivery through category handlers. Preserve constrained generic calls; avoid payload boxing, reflection invocation, per-event closures, temporary collections, and type-dictionary lookups on these paths. Initialization, growth, subscription changes, reporting, and application allocations are outside this guarantee.

   Preserve value-type scheduler entries: storing derived channels directly in a base-reference array can grow CoreCLR's shared cast cache and allocate after bus warmup. Allocation checks must also verify delivery counts so a stopped event chain cannot produce a false pass.

10. **Unity lifecycle:** reset session state at `SubsystemRegistration`; register generated metadata at `AfterAssembliesLoaded`; establish readiness at `BeforeSplashScreen`; install the bus at `BeforeSceneLoad`. Keep one callback immediately after `Update.ScriptRunBehaviourUpdate`, preserve other systems, and remove it during teardown. Recreate mutable bus state each Play session while safely retaining immutable routing metadata when domain reload is disabled.

## Code and documentation style

- Follow [.editorconfig](.editorconfig): four spaces, multiline braces and method bodies, logical whitespace, and final newlines.
- Use braces for every control-flow body, including one-statement `if`, `for`, `foreach`, `while`, and `using` blocks. Avoid compressed one-line statements and expression-bodied methods; normal auto-properties are fine.
- Document every public declaration with XML comments suitable for Visual Studio and Doxygen, including public members on internal helper types. Include relevant `summary`, `param`, `typeparam`, `returns`, `value`, `remarks`, and `exception` elements. Use valid `inheritdoc` when appropriate. Do not silence missing-documentation diagnostics to make a build pass.
- Keep methods focused and separate logical steps with whitespace. Explain snapshot boundaries, reference lifetimes, generic constraints, and other non-obvious invariants in comments.
- Use regions only when they separate meaningful groups in a longer file. Avoid decorative regions around every member.
- Keep examples consistent with the same conventions. Update both READMEs, the API guide, samples, and the overview SVG when their described behavior changes.
- Structure refactors so they do not introduce extra large-struct copies or allocations merely to extract a helper. Measure affected paths when that risk exists.

## Build and validation workflow

Run commands from the repository root. Initial generator dependency restore needs NuGet access. The maintained development harness needs the .NET 9 SDK; using the Unity package does not.

For core changes, start with:

```powershell
dotnet build Tools/QueuedEventBus.Core/QueuedEventBus.Core.csproj -c Release
dotnet run --project Tests/QueuedEventBus.Tests -c Release
```

For generator, analyzer, or generated-routing changes:

```powershell
dotnet build Tools/QueuedEventBus.Generator/QueuedEventBus.Generator.csproj -c Release
dotnet run --project Tools/QueuedEventBus.Generator.Tests -c Release
```

The generator's Release build copies its DLL into `Packages/com.eventbus.queued/Runtime/Analyzers/`. Include the rebuilt DLL with generator source changes. Preserve its `.meta` GUID, `RoslynAnalyzer` label, and disabled runtime/editor plugin platform settings. Unity supplies compiler dependencies.

Use the common runner for broader checks:

```powershell
./Tests/run-tests.ps1 -Mode All
./Tests/run-tests.ps1 -Mode PlayMode -NoDomainReload
./Tests/run-tests.ps1 -Mode Benchmark
./Tests/run-tests.ps1 -Mode BenchmarkContext
./Tests/run-tests.ps1 -Mode IL2CPP
```

`All` runs Core, Generator, EditMode, and normal PlayMode; it does not include the two benchmark matrices or IL2CPP. Run the explicit no-domain-reload configuration separately. Individual `Core`, `Generator`, `EditMode`, and `PlayMode` modes are available.

- **Core contracts/routing/queues:** run the core harness and relevant generator and Unity suites. Cover exact/category paths, contexts, reentrancy, subscriber changes, reference release, and cross-assembly behavior when affected.
- **Unity lifecycle/PlayerLoop:** run EditMode and both PlayMode configurations. Do not infer lifecycle correctness from standalone tests.
- **Hot-path or queue-layout changes:** run both benchmark modes as well as allocation assertions. They report enqueue and dispatch separately across payload sizes, event-type counts, and listener counts. Compare like environments; noisy .NET timings are not Unity or IL2CPP throughput claims.
- **Generated adapters or stripping:** player validation needs IL2CPP and high managed stripping. Current player validation remains incomplete because the module/native toolchain was unavailable. Report that limitation rather than implying editor tests establish player support.
- **Documentation-only changes:** check links, examples, and any changed visual. Render and inspect modified SVGs. Runtime tests need not be rerun for prose changes.

Serialize Unity runs against `TestProject`; they share project locks, settings, logs, and result paths. The runner changes domain-reload/player settings, so review resulting tracked changes and keep only intended ones. Coordinate generator builds that overwrite the bundled DLL with Unity compilation, and avoid concurrent builds sharing output directories.

The runner defaults to the local Unity 2022.3.62f3 executable. Override it with `-Unity 'C:\path\to\Unity.exe'`. If PowerShell blocks script execution, use a process-only override:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-tests.ps1 -Mode Core
```

Do not change machine-wide execution policy for these workflows. Logs, XML results, and benchmark CSVs go in `artifacts/`. Check exit codes and fresh reports; a missing or empty report is not a passing test. Report what ran, what passed, and what remains unverified.

## Package assets and handoff

- Keep existing Unity GUIDs stable. Move an asset with its `.meta` file. Do not recreate existing importer metadata or discard the analyzer settings.
- After adding package assets, generate missing metadata:

```powershell
./Tools/Update-PackageMetadata.ps1
```

- When a distributable archive is needed, create it locally after code, docs, and generator artifacts agree:

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
npm pack ./Packages/com.eventbus.queued --pack-destination ./artifacts --ignore-scripts --offline
```

- Inspect the archive contents and embedded documentation links. The package includes runtime/editor code, the analyzer DLL/importer, tests, samples, and docs; generator source, development projects, and test-host caches stay outside it. Packing is not publishing.
- Finish with a focused diff review, relevant validation results, and any concrete compatibility limitations. Do not treat ignored preview/build utilities as maintained repository tools.
