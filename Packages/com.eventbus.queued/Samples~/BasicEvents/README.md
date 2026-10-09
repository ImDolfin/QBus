# Basic events

Import this sample from Package Manager. Add `CombatPublisher`, `DamageListener`, and `CombatListener` to an active GameObject in an empty scene, then enter Play mode.

`CombatPublisher.Start` queues one damage event and one heal event, then calls `Events.RaiseImmediate` for a second damage event. The immediate event invokes its exact and category callbacks before `Start` returns. After `Update`, the first damage event invokes its exact and category callbacks, followed by the heal event's category callback. Immediate delivery can overtake queued events, including events of its own type; it leaves their queues untouched. Every type implements `IEvent` through `ICombatEvent`; generated routing requires no hand-written channel registration.

Both listeners subscribe in `OnEnable` and dispose their tokens in `OnDisable`. The generic combat callback retains each event's concrete type when accessing the category's `TargetId` property. Sample logging allocates; remove logging when measuring bus allocations.

Add `MetadataListener` to see the optional two-parameter exact callback and `IEventHandlerWithContext<ICombatEvent>` category handler. Each raise automatically captures a timestamp and a per-bus sequence number. The two callbacks for a damage publication receive the same metadata. The sample subtracts the raise timestamp from `Stopwatch.GetTimestamp()` and divides by `EventContext.TimestampFrequency` to measure elapsed queue time; timestamps are independent of Unity's time scale and are not UTC values.

Sequence numbers identify publications within the current bus lifetime, starting at one. Queued and immediate publications share this sequence: in this sample, publication 3 is delivered before publications 1 and 2. Sequences restart with each Play session. Queued events retain FIFO order per concrete type.

Choose `RaiseImmediate` only when the subscribers must finish before the publisher continues. An immediate raise from a listener invokes nested callbacks synchronously, up to 64 active immediate publications. An ordinary `Raise` from any callback still queues work for a subsequent pump. Application logging and the sample's metadata formatting allocate in either delivery mode.
