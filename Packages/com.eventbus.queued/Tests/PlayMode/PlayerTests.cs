using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using NUnit.Framework;
using QueuedEventBus.Tests.Fixtures;
using QueuedEventBus.Tests.OtherEvents;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace QueuedEventBus.Tests.PlayMode
{
    /// <summary>
    /// Verifies automatic delivery, generated routing, and warmed allocations in a running player.
    /// </summary>
    public sealed class PlayerTests
    {
        private static int _exactCount;

        /// <summary>
        /// Verifies publication from Awake, automatic delivery, and subscription removal when the component is disabled.
        /// </summary>
        /// <returns>The coroutine that waits for PlayerLoop dispatch and component teardown.</returns>
        [UnityTest]
        public IEnumerator DefaultBusIsAvailableInAwakeAndDispatchesAutomatically()
        {
            var gameObject = new GameObject("QueuedEventBus lifecycle test");
            var probe = gameObject.AddComponent<LifecycleProbe>();
            Assert.That(probe.AwakeCompleted, Is.True);
            Assert.That(probe.Received, Is.Zero);

            // Allow the coroutine and the bus PlayerLoop stage to both advance.
            yield return null;
            yield return null;
            Assert.That(probe.Received, Is.EqualTo(1));

            gameObject.SetActive(false);
            Events.Raise(new ActivityEvent(42));

            // Allow the coroutine and the bus PlayerLoop stage to both advance.
            yield return null;
            yield return null;
            Assert.That(probe.Received, Is.EqualTo(1));
            Object.Destroy(gameObject);
        }

        /// <summary>
        /// Verifies generated category and class-hierarchy registration in the player build.
        /// </summary>
        [Test]
        public void PlayerRegistersCrossAssemblyCategoriesAndClassHierarchy()
        {
            using (var bus = new EventBus())
            {
                var handler = new CountingHandler<IActivity>();
                var classHandler = new CountingHandler<ActivityBase>();
                bus.Subscribe<IActivity>(handler);
                bus.Subscribe<ActivityBase>(classHandler);

                bus.Raise(new ActivityEvent(1));
                bus.Raise(new SecondaryEvent(2));
                bus.Raise(new CrossAssemblyDerived());
                bus.Raise(new InternalEvent());
                bus.Raise(new NestedFixture.Event());

                Assert.That(bus.Pump(), Is.EqualTo(5));
                Assert.That(handler.Count, Is.EqualTo(5));
                Assert.That(classHandler.Count, Is.EqualTo(1));
            }
        }

        /// <summary>
        /// Verifies allocation-free publication and delivery after queue and callback warmup.
        /// </summary>
        /// <param name="mode">Subscription mode: zero for exact, one for category, or two for both.</param>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void WarmedStructDispatchAllocatesZeroBytes(int mode)
        {
            using (var bus = new EventBus())
            {
                var category = new CountingHandler<IActivity>();
                if (mode != 1)
                {
                    bus.Subscribe<ActivityEvent>(Consume);
                }

                if (mode != 0)
                {
                    bus.Subscribe<IActivity>(category);
                }

                bus.Prewarm<ActivityEvent>(128);

                for (var i = 0; i < 32; i++)
                {
                    PublishAndPump(bus);
                }

                // Exclude queue growth and first-use generic initialization from the steady-state sample.
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 128; i++)
                {
                    PublishAndPump(bus);
                }

                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.That(bytes, Is.Zero, "Bus allocations after queue and callback warmup");
                if (mode != 0)
                {
                    Assert.That(category.Count, Is.EqualTo(20480));
                }
            }
        }

        /// <summary>
        /// Verifies that a generic handler reads a struct category property without allocating or boxing.
        /// </summary>
        [Test]
        public void ConstrainedCategoryPropertyAccessAllocatesZeroBytes()
        {
            using (var bus = new EventBus())
            {
                var handler = new SummingHandler();
                bus.Subscribe<IValuedEvent>(handler);
                bus.Prewarm<ValuedEvent>(1);

                for (var i = 0; i < 100; i++)
                {
                    bus.Raise(new ValuedEvent(7));
                    bus.Pump();
                }

                // Exclude queue growth and first-use generic initialization from the steady-state sample.
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10000; i++)
                {
                    bus.Raise(new ValuedEvent(7));
                    bus.Pump();
                }

                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.That(bytes, Is.Zero);
                // Both the 100 warmup events and 10,000 measured events contribute seven each.
                Assert.That(handler.Sum, Is.EqualTo(70700));
            }
        }

        /// <summary>
        /// Measures and logs category enqueue and dispatch throughput for three payload sizes.
        /// </summary>
        [Test]
        public void ReportPlayerCategoryThroughputByPayloadSize()
        {
            Measure<Payload16>(16, new Payload16(1));
            Measure<Payload64>(64, new Payload64(1));
            Measure<Payload256>(256, new Payload256(1));
        }

        private static void Measure<T>(int size, T message)
            where T : struct, IActivity
        {
            const int eventCount = 32768;
            using (var bus = new EventBus())
            {
                var category = new CountingHandler<IActivity>();
                bus.Subscribe<IActivity>(category);
                bus.Prewarm<T>(eventCount);

                for (var i = 0; i < eventCount; i++)
                {
                    bus.Raise(in message);
                }

                bus.Pump();
                // Exclude queue growth and first-use generic initialization from the steady-state sample.
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < eventCount; i++)
                {
                    bus.Raise(in message);
                }

                var enqueued = Stopwatch.GetTimestamp();
                bus.Pump();
                var end = Stopwatch.GetTimestamp();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.That(bytes, Is.Zero);
                Assert.That(category.Count, Is.EqualTo(eventCount * 2));

                LogMeasurements(size, eventCount, enqueued - start, end - enqueued, bytes);
            }
        }

        private static void LogMeasurements(int size, int count, long enqueueTicks, long dispatchTicks, long bytes)
        {
            var enqueueNs = enqueueTicks * 1e9 / Stopwatch.Frequency / count;
            var dispatchNs = dispatchTicks * 1e9 / Stopwatch.Frequency / count;

            UnityEngine.Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "QueuedEventBus {0} {1}: payload={2} bytes category enqueue={3:F2} ns/event " +
                "dispatch={4:F2} ns/event allocations={5}",
                Application.unityVersion, Application.platform, size, enqueueNs, dispatchNs, bytes));
        }

        private static void PublishAndPump(EventBus bus)
        {
            for (var i = 0; i < 128; i++)
            {
                bus.Raise(new ActivityEvent(i));
            }

            bus.Pump();
        }

        private static void Consume(in ActivityEvent message)
        {
            _exactCount++;
        }

        private sealed class CountingHandler<TBase> : IEventHandler<TBase> where TBase : IEvent
        {
            /// <summary>
            /// The number of callback invocations observed by this handler.
            /// </summary>
            public int Count;

            /// <summary>
            /// Counts one delivered event without reading or boxing its payload.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered event passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : TBase
            {
                Count++;
            }
        }

        private sealed class SummingHandler : IEventHandler<IValuedEvent>
        {
            /// <summary>
            /// The accumulated values read through the event category constraint.
            /// </summary>
            public int Sum;

            /// <summary>
            /// Adds the category property through the concrete generic event constraint.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered event passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : IValuedEvent
            {
                Sum += message.Value;
            }
        }
    }

    /// <summary>
    /// Observes default-bus availability and subscription teardown during Unity object lifecycle callbacks.
    /// </summary>
    public sealed class LifecycleProbe : MonoBehaviour
    {
        private Subscription _subscription;

        /// <summary>
        /// Gets whether Awake successfully published through the default bus.
        /// </summary>
        public bool AwakeCompleted
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the number of messages delivered while the component was enabled.
        /// </summary>
        public int Received
        {
            get;
            private set;
        }

        private void Awake()
        {
            Events.Prewarm<ActivityEvent>(16);
            Events.Raise(new ActivityEvent(42));
            AwakeCompleted = true;
        }

        private void OnEnable()
        {
            _subscription = Events.Subscribe<ActivityEvent>(Receive);
        }

        private void OnDisable()
        {
            _subscription.Dispose();
        }

        private void Receive(in ActivityEvent message)
        {
            Received++;
        }
    }
}
