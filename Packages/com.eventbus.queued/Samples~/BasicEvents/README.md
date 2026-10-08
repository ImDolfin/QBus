# Basic events

Import this sample from Package Manager. Add `CombatPublisher`, `DamageListener`, and `CombatListener` to an active GameObject in an empty scene, then enter Play mode.

`CombatPublisher.Start` queues one damage event and one heal event. After `Update`, the Console shows one exact damage callback and two category callbacks. Every type implements `IEvent` through `ICombatEvent`; generated routing requires no hand-written channel registration.

Both listeners subscribe in `OnEnable` and dispose their tokens in `OnDisable`. The generic combat callback retains each event's concrete type when accessing the category's `TargetId` property. Sample logging allocates; remove logging when measuring bus allocations.

Add `MetadataListener` to see the optional two-parameter exact callback and `IEventHandlerWithContext<ICombatEvent>` category handler. Each raise automatically captures a timestamp and a per-bus sequence number. The two callbacks for a damage publication receive the same metadata. The sample subtracts the raise timestamp from `Stopwatch.GetTimestamp()` and divides by `EventContext.TimestampFrequency` to measure elapsed queue time; timestamps are independent of Unity's time scale and are not UTC values.

Sequence numbers identify publications within the current bus lifetime, starting at one. They restart with each Play session and do not change the per-channel delivery ordering.
