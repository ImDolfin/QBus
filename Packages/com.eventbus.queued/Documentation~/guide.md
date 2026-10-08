# API and behavior guide

## Choose an owner

`Events` is a static facade over one automatically owned Unity bus. It is ready before scene `Awake`, delivers immediately after `Update.ScriptRunBehaviourUpdate`, and shuts down when Play mode or the application exits. Each Play session gets a new bus even with domain reload disabled. Generated immutable routing metadata can remain registered between sessions.

Access before initialization or after teardown throws `InvalidOperationException` before changing bus state. Do not subscribe from static field initializers, editor callbacks, or early runtime initialization callbacks. For EditMode tooling, use an owned bus:

```csharp
using (var bus = new EventBus(exception => UnityEngine.Debug.LogException(exception)))
{
    var token = bus.Subscribe<DamageEvent>(OnDamage);
    bus.Raise(new DamageEvent(10));
    int deliveredEvents = bus.Pump();
    token.Dispose();
}
```

An owned bus has no PlayerLoop registration. Its owner controls its lifetime and pump timing. `Dispose` releases queued payloads and subscriptions; disposing a subscription token more than once, including copied tokens, is safe. Dispose listener tokens on disable or destruction so listeners do not remain retained. The default bus's exception reporter starts as `Debug.LogException` each Play session; assign `Events.ExceptionReporter` to customize it.

## API

| Operation | Meaning |
| --- | --- |
| `Raise<T>(in T message)` | Capture publication metadata and enqueue one concrete event. |
| `Subscribe<T>(EventCallback<T> callback)` | Register an exact-type callback with an `in T` parameter. |
| `Subscribe<T>(EventCallbackWithContext<T> callback)` | Register an exact-type callback with `in T` and `in EventContext` parameters. |
| `Subscribe<TBase>(IEventHandler<TBase> handler)` | Register a category handler with generic `Handle<T>(in T)` where `T : TBase`. |
| `Subscribe<TBase>(IEventHandlerWithContext<TBase> handler)` | Register a category handler with generic `Handle<T>(in T, in EventContext)` where `T : TBase`. |
| `Prewarm<T>(int capacity)` | Create the queue and reserve at least the requested capacity plus scheduling storage. |
| `Subscription.Dispose()` | Remove that one subscription. |
| `EventBus.Pump()` | Deliver the starting snapshot and return the number of events dequeued. |
| `EventBus.Dispose()` | Release the owned bus. |

Payload types and category types implement `IEvent`. The bus supports class and struct payloads; immutable structs are recommended. Raise using the concrete type: `IEvent message = new DamageEvent(10); Events.Raise(message)` is invalid. A derived class passed under a non-abstract base event type is also rejected. Null class payloads are rejected.

`in` protects the callback parameter from reassignment; it does not deep-freeze referenced objects. Keep reference payloads and any referenced fields stable until delivered. Notifications have no cancellation, consumption, or mutable propagation semantics.

## Publication context

The bus captures an immutable `EventContext` during every accepted raise and stores it alongside that publication's payload. Events continue to implement `IEvent`; they need no metadata properties or class inheritance. Metadata is available to late subscribers because it is captured at publication time while subscribers are selected at delivery time. All matching context-aware callbacks receive the same values for a publication, including exact and category callbacks from different assemblies.

| Member | Meaning |
| --- | --- |
| `long RaisedTimestamp` | The raw `Stopwatch.GetTimestamp()` reading captured during `Raise`. |
| `long Sequence` | The accepted publication's increasing identity within this bus, starting at one. |
| `static long TimestampFrequency` | Timestamp clock ticks per second, equal to `Stopwatch.Frequency`. |

Use differences between timestamps from the same clock to measure elapsed time:

```csharp
static void OnDamage(in DamageEvent message, in EventContext context)
{
    long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - context.RaisedTimestamp;
    double elapsedSeconds = elapsedTicks / (double)EventContext.TimestampFrequency;

    // Observe elapsedSeconds and context.Sequence without modifying the event payload.
}
```

Register that method with `Events.Subscribe<DamageEvent>(OnDamage)` or the equivalent owned-bus method. The callback type is inferred from its two parameters. For category metadata, use the optional context handler interface:

```csharp
public sealed class CombatAudit : IEventHandlerWithContext<ICombatEvent>
{
    /// <summary>Observes combat payloads and their publication metadata.</summary>
    /// <typeparam name="T">The concrete combat event type.</typeparam>
    /// <param name="message">The concrete event payload.</param>
    /// <param name="context">The metadata captured at raise time.</param>
    public void Handle<T>(in T message, in EventContext context) where T : ICombatEvent
    {
        // Reading message.TargetId preserves constrained struct access without boxing.
        // context.Sequence is shared with exact listeners for this publication.
    }
}
```

Subscribe with `Events.Subscribe<ICombatEvent>(new CombatAudit())` and dispose the token when its owner stops listening. Existing `EventCallback<T>` and `IEventHandler<TBase>` remain valid. Payload-only and context-aware subscriptions can coexist and participate in the same subscription-order and snapshot rules. If a handler implements both interfaces for the same category, cast it to the desired interface at the subscription call to select the overload.

Timestamps have an arbitrary clock origin. They are not UTC dates, `DateTime` ticks, or Unity's scaled game time. A timestamp difference is meaningful within the platform clock's lifetime; do not persist these readings as dates or compare them between machines. Use floating-point conversion after subtraction to avoid integer truncation. The captured timestamp marks acceptance during `Raise`; elapsed time observed in a callback also includes time spent delivering earlier callbacks.

One bus sequence spans all event types and continues across pumps. Rejected publications do not consume sequence numbers. A new owned bus starts again at one, and the default bus restarts its sequence each Play session, including when domain reload is disabled. Sequences identify publication order, not global delivery order: raising `A`, `B`, `A` can deliver sequences `1`, `3`, `2` because each channel drains its captured FIFO snapshot. Identifiers are not globally unique; the bus throws `OverflowException` rather than wrapping after `long.MaxValue`. A default-initialized `EventContext` has sequence zero and does not represent a publication.

Each raise of a reused class object has a separate context. Metadata never mutates the class payload, so a second raise cannot overwrite the first publication's timestamp or sequence. The payload itself remains caller-owned and shared by reference. Contexts contain no object references and can be copied for later diagnostics. UTC, Unity frame counts, correlation IDs, and causation IDs are not populated automatically; add application-specific values to the event payload when needed.

## Delivery and subscriptions

Each concrete type has one retained ring buffer, initially sized for 16 payloads and doubled when full. Base classes and interfaces are subscription groups over concrete queues, not extra payload queues. A channel activates on its first queued event; activated channels run in activation order. FIFO is guaranteed per channel, not across channels.

Every pump captures the pending count of every currently active channel before invoking callbacks. It drains that entire snapshot, without a time or count budget. Events raised by a callback always wait until the next pump, including events targeting a channel that has not yet been visited or a brand-new channel. Large backlogs or slow callbacks can extend a frame. Recursive `Pump()` and disposal of a bus during its dispatch are rejected.

Subscriptions are selected when each event is delivered. Subscribing after an event is raised but before delivery receives it. Callbacks for an event run in subscription order across exact and category subscriptions. The callback list is captured once per event: subscribe/unsubscribe during a callback affects subsequent events, while callbacks already captured for the current event still run. The bus does not collapse separately registered subscriptions for the same handler.

Each listener exception is reported independently and later listeners still run. A failing exception reporter is contained too. Exception handling is outside the warmed allocation contract. Publication, subscriptions, pumping, and disposal use the bus's owning thread; concurrent producer queues and Unity Jobs/Burst integration are outside version one.

## Generated routing

The included generator emits descriptors and typed adapters for each source assembly's concrete events. Category adapters preserve the concrete generic payload type, allowing `IEventHandler<ICombatEvent>.Handle<DamageEvent>` and the corresponding `IEventHandlerWithContext<ICombatEvent>` call without boxing `DamageEvent` into an interface.

Supported declarations are public/internal, non-generic concrete classes and structs, including accessible non-generic nested types. Categories can be event base classes or event interfaces. All distinct matching bases/interfaces, including `IEvent`, are routed once per subscription even through diamond inheritance. Event declarations need no attributes or `partial`.

Private/protected nested events, generic event declarations, and unsupported enclosing generic types produce actionable compile-time diagnostics. Publish using concrete type arguments; the generator diagnoses statically erased publication types. If generation was absent, missing metadata fails clearly at runtime. Events contained only in third-party DLLs without generated metadata are not automatically discovered.

Generated registration runs in `AfterAssembliesLoaded`; the facade marks metadata ready in `BeforeSplashScreen` and installs before scene initialization. Editor registration supports explicit buses in EditMode. Runtime registration and generated adapters are preserved for managed stripping; the runtime avoids reflection invocation for dispatch.

The generator targets .NET Standard 2.0 and Roslyn 3.8, as specified by [Unity's 2022.3 generator documentation](https://docs.unity3d.com/2022.3/Documentation/Manual/roslyn-analyzers.html). If metadata is missing, first confirm the bundled DLL's `RoslynAnalyzer` asset label is intact and the consumer assembly references `QueuedEventBus.Runtime`.

## PlayerLoop integration

The package reads the current PlayerLoop, removes only its own marker nodes, and inserts one callback immediately after `Update.ScriptRunBehaviourUpdate`. It preserves other custom systems and does not restore Unity's default loop during cleanup. A custom loop that removes `ScriptRunBehaviourUpdate` is incompatible and produces a clear initialization error. If another package replaces the entire loop later, that package must preserve the installed callback.

## Validate performance on the target platform

Prewarm each event type for the largest expected pending burst, install subscriptions, and run warm-up publication/delivery cycles before measuring. Reuse callback delegates and handlers. Measure enqueue and dispatch separately with realistic payload sizes, type counts, and listener counts. Avoid logging, closures, string formatting, allocating payload fields, and boxing in measured application code.

Each retained queue entry stores its typed payload and 16 bytes of context data, with possible additional alignment padding. Metadata is captured for every raise, including when only payload-only listeners are registered. Reading the timestamp clock and moving a larger queue entry adds work without creating a per-event managed object. Capacity growth remains an allocation, and retained memory increases with reserved queue capacity. Benchmark the metadata path directly instead of assuming unchanged throughput from payload-only queue implementations.

The supplied correctness and warmed-allocation tests cover exact, category, and combined struct delivery both with and without context callbacks. Unity includes category throughput measurements that read context fields for 16-, 64-, and 256-byte payloads. Run the player tests with IL2CPP and managed stripping enabled before relying on a target configuration. IL2CPP generic dispatch can have different throughput characteristics from Mono; [Unity documents the relevant AOT restrictions](https://docs.unity3d.com/2022.3/Documentation/Manual/ScriptingRestrictions.html).

This package does not claim a measured throughput number or universal IL2CPP validation. A Windows IL2CPP player test requires the matching Unity Windows Build Support (IL2CPP) module and the native build toolchain.

See the [validation record](validation.md) for the tested editor/runtime configurations and remaining player validation.
