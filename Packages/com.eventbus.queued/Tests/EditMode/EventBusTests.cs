using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using QueuedEventBus.Tests.Fixtures;
using QueuedEventBus.Tests.OtherEvents;

namespace QueuedEventBus.Tests.EditMode
{
    /// <summary>
    /// Verifies generated routing, queue snapshots, subscriptions, and payload lifetime in EditMode.
    /// </summary>
    public sealed class EventBusTests
    {
        /// <summary>
        /// Verifies cross-assembly and nested event registration, including deduplicated diamond categories.
        /// </summary>
        [Test]
        public void GeneratedRegistrationWorksAcrossAssembliesAndDiamondInheritance()
        {
            using (var bus = new EventBus())
            {
                var activity = new Recorder<IActivity>();
                var all = new Recorder<IEvent>();
                bus.Subscribe<IActivity>(activity);
                bus.Subscribe<IEvent>(all);

                bus.Raise(new ActivityEvent(1));
                bus.Raise(new SecondaryEvent(2));
                bus.Raise(new NestedFixture.Event());
                bus.Raise(new InternalEvent());

                Assert.That(bus.Pump(), Is.EqualTo(4));
                Assert.That(activity.Types, Is.EqualTo(new[]
                {
                    typeof(ActivityEvent), typeof(SecondaryEvent), typeof(NestedFixture.Event), typeof(InternalEvent)
                }));
                Assert.That(all.Types, Is.EqualTo(activity.Types));
            }
        }

        /// <summary>
        /// Verifies that category handlers receive base and derived instances while exact handlers receive only the base type.
        /// </summary>
        [Test]
        public void ClassCategoriesIncludeBaseAndCrossAssemblyDescendants()
        {
            using (var bus = new EventBus())
            {
                var categories = new Recorder<ActivityBase>();
                var exactCount = 0;
                bus.Subscribe<ActivityBase>(categories);
                bus.Subscribe<ActivityBase>((in ActivityBase item) => exactCount++);

                bus.Raise(new ActivityBase());
                bus.Raise(new DerivedActivity());
                bus.Raise(new CrossAssemblyDerived());

                Assert.That(bus.Pump(), Is.EqualTo(3));
                Assert.That(exactCount, Is.EqualTo(1));
                Assert.That(categories.Types, Is.EqualTo(new[]
                {
                    typeof(ActivityBase), typeof(DerivedActivity), typeof(CrossAssemblyDerived)
                }));
            }
        }

        /// <summary>
        /// Verifies that callback publications wait for the next pump even when their channels already exist.
        /// </summary>
        [Test]
        public void SnapshotDefersReentrantMessagesForAllChannels()
        {
            using (var bus = new EventBus())
            {
                var values = new List<int>();
                bus.Subscribe<ActivityEvent>((in ActivityEvent item) =>
                {
                    values.Add(item.Value);
                    if (item.Value != 1)
                    {
                        return;
                    }

                    bus.Raise(new ActivityEvent(3));
                    bus.Raise(new SecondaryEvent(4));
                    bus.Raise(new NestedFixture.Event());
                });
                bus.Subscribe<SecondaryEvent>((in SecondaryEvent item) => values.Add(item.Value));

                bus.Raise(new ActivityEvent(1));
                bus.Raise(new SecondaryEvent(2));

                Assert.That(bus.Pump(), Is.EqualTo(2));
                Assert.That(values, Is.EqualTo(new[] { 1, 2 }));
                // The nested event has no listener, but its queued message still counts as pumped.
                Assert.That(bus.Pump(), Is.EqualTo(3));
                Assert.That(values, Is.EqualTo(new[] { 1, 2, 3, 4 }));
                Assert.That(bus.Pump(), Is.Zero);
            }
        }

        /// <summary>
        /// Verifies FIFO delivery while queue storage grows and its read position wraps.
        /// </summary>
        [Test]
        public void QueueGrowsAndWrapsWithoutChangingFifo()
        {
            using (var bus = new EventBus())
            {
                var values = new List<int>();
                bus.Subscribe<ActivityEvent>((in ActivityEvent item) => values.Add(item.Value));
                for (var round = 0; round < 20; round++)
                {
                    for (var i = 0; i < 103; i++)
                    {
                        bus.Raise(new ActivityEvent(round * 103 + i));
                    }

                    Assert.That(bus.Pump(), Is.EqualTo(103));
                }

                for (var i = 0; i < values.Count; i++)
                {
                    Assert.That(values[i], Is.EqualTo(i));
                }
            }
        }

        /// <summary>
        /// Verifies that subscription mutations affect subsequent messages while the current delivery retains its callbacks.
        /// </summary>
        [Test]
        public void ListenerMutationUsesOneSnapshotPerMessage()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                Subscription second = default;
                bus.Subscribe<ActivityEvent>((in ActivityEvent item) =>
                {
                    calls.Add("first " + item.Value);
                    if (item.Value != 1)
                    {
                        return;
                    }

                    second.Dispose();
                    bus.Subscribe<IActivity>(new NamedRecorder(calls));
                });
                second = bus.Subscribe<ActivityEvent>((in ActivityEvent item) => calls.Add("second " + item.Value));
                bus.Raise(new ActivityEvent(1));
                bus.Raise(new ActivityEvent(2));
                bus.Pump();

                // Removing the second listener cannot change the callback snapshot already in flight.
                Assert.That(calls, Is.EqualTo(new[] { "first 1", "second 1", "first 2", "new" }));
            }
        }

        /// <summary>
        /// Verifies that subscriber selection occurs during delivery rather than publication.
        /// </summary>
        [Test]
        public void SubscribersAddedAfterPublicationReceivePendingMessages()
        {
            using (var bus = new EventBus())
            {
                bus.Raise(new ActivityEvent(1));
                var recorder = new Recorder<IActivity>();
                bus.Subscribe<IActivity>(recorder);
                bus.Pump();

                Assert.That(recorder.Types.Count, Is.EqualTo(1));
            }
        }

        /// <summary>
        /// Verifies independent duplicate subscriptions and idempotent disposal of copied subscription tokens.
        /// </summary>
        [Test]
        public void DuplicateSubscriptionsAndCopiedTokensAreIndependent()
        {
            using (var bus = new EventBus())
            {
                var count = 0;
                EventCallback<ActivityEvent> listener = (in ActivityEvent item) => count++;
                var first = bus.Subscribe(listener);
                var copy = first;
                var second = bus.Subscribe(listener);
                bus.Raise(new ActivityEvent());
                bus.Pump();

                Assert.That(count, Is.EqualTo(2));
                first.Dispose();
                copy.Dispose();
                bus.Raise(new ActivityEvent());
                bus.Pump();

                Assert.That(count, Is.EqualTo(3));
                second.Dispose();
            }
        }

        /// <summary>
        /// Verifies that a failing callback is reported without interrupting remaining deliveries.
        /// </summary>
        [Test]
        public void ListenerExceptionDoesNotStopOtherSubscribers()
        {
            var errors = new List<Exception>();
            using (var bus = new EventBus(errors.Add))
            {
                var count = 0;
                bus.Subscribe<ActivityEvent>((in ActivityEvent item) => throw new InvalidOperationException("expected"));
                bus.Subscribe<ActivityEvent>((in ActivityEvent item) => count++);

                bus.Raise(new ActivityEvent());
                bus.Raise(new ActivityEvent());

                Assert.That(bus.Pump(), Is.EqualTo(2));
                Assert.That(errors.Count, Is.EqualTo(2));
                Assert.That(count, Is.EqualTo(2));
            }
        }

        /// <summary>
        /// Verifies that null and erased class messages are rejected without queueing a payload.
        /// </summary>
        [Test]
        public void InvalidClassPublicationFailsBeforeEnqueueing()
        {
            using (var bus = new EventBus())
            {
                Assert.Throws<ArgumentNullException>(() => bus.Raise<ActivityBase>(null));
                ActivityBase erased = new DerivedActivity();
                Assert.Throws<ArgumentException>(() => bus.Raise(erased));
                Assert.That(bus.Pump(), Is.Zero);
            }
        }

        /// <summary>
        /// Verifies that delivered and abandoned queue entries release their payload references.
        /// </summary>
        [Test]
        public void DequeueAndDisposeReleasePayloadReferences()
        {
            var bus = new EventBus();
            var delivered = EnqueueReference(bus);
            bus.Pump();
            var pending = EnqueueReference(bus);
            bus.Dispose();
            // Keep both weak references, but no strong payload references, across collection.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(delivered.IsAlive, Is.False);
            Assert.That(pending.IsAlive, Is.False);
            // Collection must succeed even while the disposed bus itself remains reachable.
            GC.KeepAlive(bus);
        }

        // End the local payload lifetime in a separate stack frame before forcing collection.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference EnqueueReference(EventBus bus)
        {
            var payload = new object();
            bus.Raise(new ReferenceEvent { Payload = payload });
            return new WeakReference(payload);
        }

        private sealed class Recorder<TBase> : IEventHandler<TBase> where TBase : IEvent
        {
            /// <summary>
            /// The concrete event types observed in delivery order.
            /// </summary>
            public readonly List<Type> Types = new List<Type>();

            /// <summary>
            /// Records the concrete event type in delivery order.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered event passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : TBase
            {
                Types.Add(typeof(T));
            }
        }

        private sealed class NamedRecorder : IEventHandler<IActivity>
        {
            private readonly List<string> _calls;

            /// <summary>
            /// Creates a recorder used by subscription-mutation tests.
            /// </summary>
            /// <param name="calls">The list receiving the new-listener marker.</param>
            public NamedRecorder(List<string> calls)
            {
                _calls = calls;
            }

            /// <summary>
            /// Appends the new-listener marker for this delivery.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered event passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : IActivity
            {
                _calls.Add("new");
            }
        }
    }
}
