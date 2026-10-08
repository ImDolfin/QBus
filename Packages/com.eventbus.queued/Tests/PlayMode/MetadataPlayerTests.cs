using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using NUnit.Framework;
using QueuedEventBus.Tests.Fixtures;
using QueuedEventBus.Tests.OtherEvents;
using UnityEngine;
using UnityEngine.TestTools;

namespace QueuedEventBus.Tests.PlayMode
{
    /// <summary>
    /// Verifies publication metadata through automatic routing and allocation-free player delivery.
    /// </summary>
    public sealed class MetadataPlayerTests
    {
        private static long _exactSequenceSum;
        private static long _lastExactTimestamp;
        private static int _exactValueSum;

        /// <summary>
        /// Verifies shared raise-time metadata and mixed subscription order through the default bus.
        /// </summary>
        /// <returns>The coroutine that waits for automatic PlayerLoop delivery.</returns>
        [UnityTest]
        public IEnumerator DefaultBusDeliversContextsAcrossAssembliesInSubscriptionOrder()
        {
            var calls = new List<string>();
            var exactContexts = new List<EventContext>();
            var category = new RecordingHandler(calls);
            var raisedBefore = Stopwatch.GetTimestamp();

            using (Events.Subscribe<ActivityEvent>((in ActivityEvent message) =>
            {
                calls.Add("payload");
            }))
            {
                using (Events.Subscribe<IActivity>(category))
                {
                    using (Events.Subscribe<ActivityEvent>((in ActivityEvent message, in EventContext context) =>
                    {
                        calls.Add("context");
                        exactContexts.Add(context);
                    }))
                    {
                        Events.Raise(new ActivityEvent(1));
                        Events.Raise(new SecondaryEvent(2));
                        Events.Raise(new ActivityEvent(3));
                        var raisedAfter = Stopwatch.GetTimestamp();

                        yield return null;
                        yield return null;

                        CollectionAssert.AreEqual(
                            new[] { "payload", "ActivityEvent", "context", "payload", "ActivityEvent", "context", "SecondaryEvent" },
                            calls);
                        Assert.That(exactContexts.Count, Is.EqualTo(2));
                        Assert.That(category.Contexts.Count, Is.EqualTo(3));

                        AssertMatchingContext(exactContexts[0], category.Contexts[0]);
                        AssertMatchingContext(exactContexts[1], category.Contexts[1]);

                        // Sequence tracks publication across types; per-channel FIFO can deliver
                        // the third publication before the second publication's channel.
                        var firstSequence = category.Contexts[0].Sequence;
                        Assert.That(category.Contexts[1].Sequence, Is.EqualTo(firstSequence + 2));
                        Assert.That(category.Contexts[2].Sequence, Is.EqualTo(firstSequence + 1));

                        foreach (var context in category.Contexts)
                        {
                            Assert.That(context.RaisedTimestamp, Is.InRange(raisedBefore, raisedAfter));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Verifies zero warmed allocations while exact and constrained category callbacks read metadata.
        /// </summary>
        /// <param name="mode">Zero for exact callbacks, one for category handlers, or two for both.</param>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void WarmedContextDispatchAllocatesZeroBytes(int mode)
        {
            using (var bus = new EventBus())
            {
                var category = new SummingContextHandler();
                _exactSequenceSum = 0;
                _exactValueSum = 0;
                _lastExactTimestamp = 0;
                ConfigureSubscriptions(bus, category, mode);
                bus.Prewarm<ValuedEvent>(128);

                for (var i = 0; i < 32; i++)
                {
                    PublishAndPump(bus);
                }

                // The sample includes timestamp capture, typed queue storage, and callback
                // reads, while excluding channel initialization and subscription changes.
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 128; i++)
                {
                    PublishAndPump(bus);
                }

                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                const long count = 20480;
                const long expectedSequenceSum = count * (count + 1) / 2;
                Assert.That(bytes, Is.Zero);
                Assert.That(EventContext.TimestampFrequency, Is.EqualTo(Stopwatch.Frequency));

                if (mode != 1)
                {
                    Assert.That(_exactSequenceSum, Is.EqualTo(expectedSequenceSum));
                    Assert.That(_exactValueSum, Is.EqualTo(count * 7));
                    Assert.That(_lastExactTimestamp, Is.LessThanOrEqualTo(Stopwatch.GetTimestamp()));
                }

                if (mode != 0)
                {
                    Assert.That(category.SequenceSum, Is.EqualTo(expectedSequenceSum));
                    Assert.That(category.ValueSum, Is.EqualTo(count * 7));
                    Assert.That(category.LastTimestamp, Is.LessThanOrEqualTo(Stopwatch.GetTimestamp()));
                }
            }
        }

        /// <summary>
        /// Measures category delivery that consumes context with three concrete payload sizes.
        /// </summary>
        [Test]
        public void ReportPlayerContextCategoryThroughputByPayloadSize()
        {
            Measure<Payload16>(16, new Payload16(1));
            Measure<Payload64>(64, new Payload64(1));
            Measure<Payload256>(256, new Payload256(1));
        }

        private static void AssertMatchingContext(EventContext exact, EventContext category)
        {
            Assert.That(category.Sequence, Is.EqualTo(exact.Sequence));
            Assert.That(category.RaisedTimestamp, Is.EqualTo(exact.RaisedTimestamp));
        }

        private static void ConfigureSubscriptions(EventBus bus, SummingContextHandler category, int mode)
        {
            if (mode != 1)
            {
                bus.Subscribe<ValuedEvent>(Consume);
            }

            if (mode != 0)
            {
                bus.Subscribe<IValuedEvent>(category);
            }
        }

        private static void PublishAndPump(EventBus bus)
        {
            for (var i = 0; i < 128; i++)
            {
                bus.Raise(new ValuedEvent(7));
            }

            bus.Pump();
        }

        private static void Consume(in ValuedEvent message, in EventContext context)
        {
            _exactValueSum += message.Value;
            _exactSequenceSum += context.Sequence;
            _lastExactTimestamp = context.RaisedTimestamp;
        }

        private static void Measure<T>(int size, T message) where T : struct, IActivity
        {
            const int eventCount = 32768;
            using (var bus = new EventBus())
            {
                var category = new TimingHandler();
                bus.Subscribe<IActivity>(category);
                bus.Prewarm<T>(eventCount);
                PublishBatch(bus, in message, eventCount);
                bus.Pump();

                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                PublishBatch(bus, in message, eventCount);
                var enqueued = Stopwatch.GetTimestamp();
                bus.Pump();
                var end = Stopwatch.GetTimestamp();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(bytes, Is.Zero);
                Assert.That(category.Count, Is.EqualTo(eventCount * 2));
                Assert.That(category.LastSequence, Is.EqualTo(eventCount * 2));
                Assert.That(category.LastTimestamp, Is.LessThanOrEqualTo(enqueued));
                LogMeasurements(size, eventCount, enqueued - start, end - enqueued, bytes);
            }
        }

        private static void PublishBatch<T>(EventBus bus, in T message, int count) where T : struct, IActivity
        {
            for (var i = 0; i < count; i++)
            {
                bus.Raise(in message);
            }
        }

        private static void LogMeasurements(int size, int count, long enqueueTicks, long dispatchTicks, long bytes)
        {
            var enqueueNs = enqueueTicks * 1e9 / Stopwatch.Frequency / count;
            var dispatchNs = dispatchTicks * 1e9 / Stopwatch.Frequency / count;

            UnityEngine.Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "QueuedEventBus {0} {1}: payload={2} bytes context category enqueue={3:F2} ns/event " +
                "dispatch={4:F2} ns/event allocations={5}",
                Application.unityVersion, Application.platform, size, enqueueNs, dispatchNs, bytes));
        }

        private sealed class RecordingHandler : IEventHandlerWithContext<IActivity>
        {
            private readonly List<string> _calls;

            /// <summary>
            /// Stores the metadata observed in category delivery order.
            /// </summary>
            public readonly List<EventContext> Contexts = new List<EventContext>();

            /// <summary>
            /// Creates a handler that appends category callbacks to a shared call trace.
            /// </summary>
            /// <param name="calls">The shared delivery trace.</param>
            public RecordingHandler(List<string> calls)
            {
                _calls = calls;
            }

            /// <summary>
            /// Records the concrete event type and its publication metadata.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The concrete event payload.</param>
            /// <param name="context">The metadata captured when the event was raised.</param>
            public void Handle<T>(in T message, in EventContext context) where T : IActivity
            {
                _calls.Add(typeof(T).Name);
                Contexts.Add(context);
            }
        }

        private sealed class SummingContextHandler : IEventHandlerWithContext<IValuedEvent>
        {
            /// <summary>
            /// Accumulates the publication sequence numbers.
            /// </summary>
            public long SequenceSum;

            /// <summary>
            /// Accumulates values read through the concrete generic category constraint.
            /// </summary>
            public int ValueSum;

            /// <summary>
            /// Stores the most recently delivered raise timestamp.
            /// </summary>
            public long LastTimestamp;

            /// <summary>
            /// Reads the payload and context without converting the struct into an interface.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The concrete event payload.</param>
            /// <param name="context">The metadata captured when the event was raised.</param>
            public void Handle<T>(in T message, in EventContext context) where T : IValuedEvent
            {
                ValueSum += message.Value;
                SequenceSum += context.Sequence;
                LastTimestamp = context.RaisedTimestamp;
            }
        }

        private sealed class TimingHandler : IEventHandlerWithContext<IActivity>
        {
            /// <summary>
            /// Counts all benchmark callbacks.
            /// </summary>
            public int Count;

            /// <summary>
            /// Stores the last publication sequence delivered.
            /// </summary>
            public long LastSequence;

            /// <summary>
            /// Stores the last publication timestamp delivered.
            /// </summary>
            public long LastTimestamp;

            /// <summary>
            /// Consumes the metadata without allocating during the measured sample.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The concrete event payload.</param>
            /// <param name="context">The metadata captured when the event was raised.</param>
            public void Handle<T>(in T message, in EventContext context) where T : IActivity
            {
                Count++;
                LastSequence = context.Sequence;
                LastTimestamp = context.RaisedTimestamp;
            }
        }
    }
}
