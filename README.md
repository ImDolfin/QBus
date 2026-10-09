# Queued Event Bus for Unity

Define an event, subscribe, and raise it. Queued Event Bus delivers typed notifications after Unity's `Update`, or synchronously with `RaiseImmediate`. It creates queues automatically and routes events to exact-type and category subscribers without event boxing. The installable Unity 2022.3+ package is in [`Packages/com.eventbus.queued`](Packages/com.eventbus.queued).

![Queued Event Bus overview: generated routes connect concrete events to exact and category subscribers. Raise queues a payload and context for the next pump; RaiseImmediate invokes the same subscribers synchronously. Handler calls to Raise wait for the next pump.](Packages/com.eventbus.queued/Documentation~/images/eventbus-overview.svg)

## From declaration to delivery

| Stage | What happens |
| --- | --- |
| **Compile** | The bundled generator discovers events implementing `IEvent` and emits typed routes to their concrete type, event base classes, and interfaces. No attributes or manual registration are needed. |
| **Subscribe** | Register an exact callback or a generic category handler. Categories group subscriptions over concrete queues; they do not hold separate copies of events. |
| **Raise** | The bus captures the payload and an immutable `EventContext`, then enqueues them in that event type's lazily created queue. |
| **RaiseImmediate** | Capture the payload and context, then invoke the same matching subscribers before returning. Pending queues remain untouched. |
| **Pump** | After `Update`, the default bus captures the pending counts of **all active queues before any callback runs**, then drains those counts in channel activation order. |
| **Deliver** | Each event captures its current matching callbacks in one registration-order list, combining exact and category subscriptions. A callback's `Raise` waits for the next pump; `RaiseImmediate` runs synchronously. |

For example, with initially empty queues, raising `Damage #1`, `Heal #2`, then `Damage #3` delivers **`Damage #1` → `Damage #3` → `Heal #2`**. FIFO is preserved within each concrete event type; there is no global delivery order. The numbers represent publication sequences, not delivery positions.

Subscriptions are selected at delivery time. A listener added after `Raise` but before delivery can receive the event. Changes during a callback affect the next event, even within the same pump; the current event finishes its captured callback list.

Every queued or immediate raise captures an immutable `EventContext` containing a raise-time timestamp and a shared per-bus publication sequence. Keep simple payload-only subscriptions or opt into metadata with a two-parameter callback or `IEventHandlerWithContext<TBase>`. Events still implement `IEvent`, including immutable structs; no base class or mutable metadata fields are required.

```csharp
Events.Raise(new DamageEvent(10));          // Deliver at the next automatic pump.
Events.RaiseImmediate(new DamageEvent(20)); // Deliver before this call returns.
```

Immediate delivery can overtake queued events, including those of the same type. Nested immediate calls finish before the invoking handler resumes, up to 64 active immediate publications per bus. `Pump()` and bus disposal are rejected during either kind of dispatch. The same APIs are available on an owned `EventBus`; see [immediate delivery](Packages/com.eventbus.queued/Documentation~/guide.md#immediate-delivery) for ordering and callback rules.

Use the automatically managed `Events` facade from scene `Awake` onward. For an independently owned bus, create `new EventBus()`, call `Pump()` explicitly, and dispose it during teardown. All operations run on the bus's owning thread.

Start with the [package README](Packages/com.eventbus.queued/README.md) or the [API and behavior guide](Packages/com.eventbus.queued/Documentation~/guide.md). Import **Basic Events** through Package Manager for concrete-event, category-handler, publication-context, and MonoBehaviour lifetime examples.

The [validation record](Packages/com.eventbus.queued/Documentation~/validation.md) lists the passing .NET and Unity checks and the outstanding IL2CPP player prerequisites.

The repository keeps generator source and development/test tooling outside the installable package. The package includes its generator DLL and Unity importer metadata, so consuming projects do not need a .NET SDK or manual analyzer setup.

The performance target is zero bus allocations during warmed publication and delivery when capacity is sufficient and subscriptions remain unchanged. Initialization, growth, subscription changes, exceptions, and callback allocations are excluded. Throughput and IL2CPP performance must be measured in target players; no numerical throughput claim is made.

## Build and validate

The development harness requires the .NET 9 SDK. Rebuild the bundled .NET Standard 2.0 / Roslyn 3.8 generator with:

```powershell
dotnet build Tools/QueuedEventBus.Generator/QueuedEventBus.Generator.csproj -c Release
```

The Release build copies the generator DLL into the package. Preserve its existing importer `.meta` file. NuGet access is needed for the generator's initial dependency restore.

Public C# APIs use XML documentation for IDE tooltips and documentation generators. The core and generator builds emit XML documentation and reject missing public documentation. The shared `.editorconfig` records brace, member-body, indentation, and spacing conventions. Keep control-flow bodies braced and multiline, and explain the queue/routing invariants when changing their implementation.

Run validation from the repository root:

```powershell
./Tests/run-tests.ps1 -Mode Core
./Tests/run-tests.ps1 -Mode Generator
./Tests/run-tests.ps1 -Mode EditMode
./Tests/run-tests.ps1 -Mode PlayMode
./Tests/run-tests.ps1 -Mode PlayMode -NoDomainReload
./Tests/run-tests.ps1 -Mode Benchmark
./Tests/run-tests.ps1 -Mode BenchmarkContext
./Tests/run-tests.ps1 -Mode IL2CPP
```

`Core` and `Generator` run without Unity. Unity modes use the included `TestProject`, which references the local package and enables its tests. The default editor path is Unity 2022.3.62f3; pass `-Unity 'C:\path\to\Unity.exe'` to select another installation. `-Mode All` runs Core, Generator, EditMode, and PlayMode. Player and benchmark runs remain explicit.

If Windows blocks local PowerShell scripts, invoke the checked-in runner with a process-only policy override: `powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-tests.ps1 -Mode Core`. This leaves the machine's execution policy unchanged.

Unity test results and logs are written to `artifacts/`; the runner rejects missing, empty, or failed reports. The PlayMode configuration helper selects domain reload behavior before launching tests. `IL2CPP` configures Windows Standalone with high managed stripping before building and running the test player. It requires Windows Build Support (IL2CPP), Visual Studio C++ build tools, a compatible Windows SDK, and an activated Unity editor.

`Benchmark` writes `artifacts/benchmarks.csv` for payload-only subscriptions. `BenchmarkContext` writes `artifacts/benchmarks-context.csv` for subscriptions consuming publication metadata. Both report enqueue/dispatch timings, delivery counts, and allocated bytes across 1, 32, and 256 event types, payload sizes, and listener counts. Every raise captures context in both modes. The harness uses five timed samples per case for quick regression checks. It runs on the local .NET runtime; Unity Mono and IL2CPP performance must be measured in their own players. Performance figures from different runtimes are not interchangeable.
