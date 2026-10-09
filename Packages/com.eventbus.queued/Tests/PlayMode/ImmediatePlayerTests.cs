using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using QueuedEventBus.Tests.Fixtures;
using QueuedEventBus.Tests.OtherEvents;
using UnityEngine.TestTools;

namespace QueuedEventBus.Tests.PlayMode
{
    /// <summary>
    /// Verifies synchronous facade delivery, generated routes, and warmed player allocations.
    /// </summary>
    public sealed class ImmediatePlayerTests
    {
        private static int _exactCount;
        private static int _exactValueSum;
        private static long _exactTimestamp;

        /// <summary>
        /// Verifies immediate struct and class delivery can overtake pending cross-assembly events.
        /// </summary>
        /// <returns>The coroutine that waits for the queued event's automatic delivery.</returns>
        [UnityTest]
        public IEnumerator ImmediateFacadeSharesMetadataAndPreservesQueuedDelivery()
        {
            var category = new RecordingHandler();
            EventContext exactContext = default;

            using (Events.Subscribe<IActivity>(category))
            {
                using (Events.Subscribe<SecondaryEvent>((in SecondaryEvent message, in EventContext context) =>
                {
                    exactContext = context;
                }))
                {
                    Events.Raise(new ActivityEvent(1));
                    var before = Stopwatch.GetTimestamp();
                    Events.RaiseImmediate(new SecondaryEvent(2));
                    var after = Stopwatch.GetTimestamp();
                    Events.RaiseImmediate(new CrossAssemblyDerived());

                    Assert.That(category.Types, Is.EqualTo(new[] { typeof(SecondaryEvent), typeof(CrossAssemblyDerived) }));
                    Assert.That(exactContext.Sequence, Is.EqualTo(category.Contexts[0].Sequence));
                    Assert.That(exactContext.RaisedTimestamp, Is.EqualTo(category.Contexts[0].RaisedTimestamp));
                    Assert.That(exactContext.RaisedTimestamp, Is.InRange(before, after));

                    yield return null;
                    yield return null;

                    Assert.That(category.Types, Is.EqualTo(new[]
                    {
                        typeof(SecondaryEvent), typeof(CrossAssemblyDerived), typeof(ActivityEvent)
                    }));
                    Assert.That(category.Contexts[1].Sequence, Is.EqualTo(exactContext.Sequence + 1));
                    Assert.That(category.Contexts[2].Sequence, Is.EqualTo(exactContext.Sequence - 1));
                }
            }
        }

        /// <summary>
        /// Verifies exact and constrained category callbacks allocate nothing during warmed facade delivery.
        /// </summary>
        /// <param name="mode">Zero for exact callbacks, one for categories, or two for both.</param>
        /// <param name="withContext">Whether both callback forms consume publication metadata.</param>
        [TestCase(0, false)]
        [TestCase(1, false)]
        [TestCase(2, false)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        public void WarmedImmediateFacadeAllocatesZeroBytes(int mode, bool withContext)
        {
            var category = new SummingHandler();
            Subscription exactToken = default;
            Subscription categoryToken = default;
            _exactCount = 0;
            _exactValueSum = 0;
            _exactTimestamp = 0;

            try
            {
                if (mode != 1)
                {
                    exactToken = withContext
                        ? Events.Subscribe<ValuedEvent>(ConsumeWithContext)
                        : Events.Subscribe<ValuedEvent>(Consume);
                }

                if (mode != 0)
                {
                    categoryToken = withContext
                        ? Events.Subscribe<IValuedEvent>((IEventHandlerWithContext<IValuedEvent>)category)
                        : Events.Subscribe<IValuedEvent>((IEventHandler<IValuedEvent>)category);
                }

                Events.Prewarm<ValuedEvent>(1);
                PublishImmediate(100);

                var before = GC.GetAllocatedBytesForCurrentThread();
                PublishImmediate(10000);
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.That(bytes, Is.Zero);
                AssertDeliveries(mode, withContext, category);
            }
            finally
            {
                exactToken.Dispose();
                categoryToken.Dispose();
            }
        }

        private static void PublishImmediate(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Events.RaiseImmediate(new ValuedEvent(7));
            }
        }

        private static void AssertDeliveries(int mode, bool withContext, SummingHandler category)
        {
            // Count real deliveries as well as allocations so an inactive facade cannot pass.
            if (mode != 1)
            {
                Assert.That(_exactCount, Is.EqualTo(10100));
                Assert.That(_exactValueSum, Is.EqualTo(70700));

                if (withContext)
                {
                    Assert.That(_exactTimestamp, Is.InRange(1, Stopwatch.GetTimestamp()));
                }
            }

            if (mode != 0)
            {
                Assert.That(category.Count, Is.EqualTo(10100));
                Assert.That(category.ValueSum, Is.EqualTo(70700));

                if (withContext)
                {
                    Assert.That(category.LastTimestamp, Is.InRange(1, Stopwatch.GetTimestamp()));
                }
            }
        }

        private static void Consume(in ValuedEvent message)
        {
            _exactCount++;
            _exactValueSum += message.Value;
        }

        private static void ConsumeWithContext(in ValuedEvent message, in EventContext context)
        {
            Consume(in message);
            _exactTimestamp = context.RaisedTimestamp;
        }

        private sealed class RecordingHandler : IEventHandlerWithContext<IActivity>
        {
            /// <summary>The concrete types observed in delivery order.</summary>
            public readonly List<Type> Types = new List<Type>();

            /// <summary>The publication contexts observed in delivery order.</summary>
            public readonly List<EventContext> Contexts = new List<EventContext>();

            /// <summary>Records the concrete event and its context.</summary>
            /// <typeparam name="T">The generated concrete event type.</typeparam>
            /// <param name="message">The delivered event payload.</param>
            /// <param name="context">The metadata captured at publication.</param>
            public void Handle<T>(in T message, in EventContext context) where T : IActivity
            {
                Types.Add(typeof(T));
                Contexts.Add(context);
            }
        }

        private sealed class SummingHandler : IEventHandler<IValuedEvent>, IEventHandlerWithContext<IValuedEvent>
        {
            /// <summary>The number of observed publications.</summary>
            public int Count;

            /// <summary>The sum read through the constrained category property.</summary>
            public int ValueSum;

            /// <summary>The last observed publication timestamp.</summary>
            public long LastTimestamp;

            /// <summary>Consumes the concrete struct without boxing it to the category.</summary>
            /// <typeparam name="T">The concrete event type.</typeparam>
            /// <param name="message">The delivered payload.</param>
            public void Handle<T>(in T message) where T : IValuedEvent
            {
                Count++;
                ValueSum += message.Value;
            }

            /// <summary>Consumes the concrete payload and its publication timestamp.</summary>
            /// <typeparam name="T">The concrete event type.</typeparam>
            /// <param name="message">The delivered payload.</param>
            /// <param name="context">The metadata captured at publication.</param>
            public void Handle<T>(in T message, in EventContext context) where T : IValuedEvent
            {
                Handle(in message);
                LastTimestamp = context.RaisedTimestamp;
            }
        }
    }
}
