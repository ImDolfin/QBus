using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace QueuedEventBus.Tests
{
    internal static class MetadataTests
    {
        private static long _sequenceSink;
        private static long _timestampSink;
        private static int _payloadSink;

        internal static void Run(Action<string, Action> run)
        {
            run("context timestamps are captured at raise and shared by all callbacks", TimestampSnapshots);
            run("context routing preserves concrete types and mixed subscription order", Routing);
            run("context class hierarchy includes concrete base and derived types", ClassRouting);
            run("context stays paired with payload through queue growth and wraparound", QueuePairing);
            run("callback-induced queue growth preserves the current payload and context", ReentrantQueueGrowth);
            run("context sequence records publication order across channels", PublicationOrder);
            run("reentrant raises of one object retain independent contexts", ReentrantClassSnapshots);
            run("context duplicate subscriptions and copied tokens are idempotent", SubscriptionTokens);
            run("context subscriber changes apply to the next event", ListenerMutation);
            run("context exact and category subscribers can unsubscribe themselves", SelfUnsubscription);
            run("context exceptions do not prevent later listeners", ExceptionIsolation);
            run("context sequence is isolated per bus and excludes rejected raises", SequenceIsolation);
            run("exhausted context sequence rejects publication without queue mutation", SequenceOverflow);
            run("context subscriptions validate null callbacks and handlers", InvalidArguments);
            run("zero warmed allocations: exact struct delivery with context", () => ZeroAllocations(0));
            run("zero warmed allocations: category struct delivery with context", () => ZeroAllocations(1));
            run("zero warmed allocations: combined struct delivery with context", () => ZeroAllocations(2));
            run("zero warmed allocations: constrained category payload and context", ConstrainedCategoryZeroAllocations);
        }

        private static void TimestampSnapshots()
        {
            using (var bus = new EventBus())
            {
                var exact = new List<EventContext>();
                var category = new List<EventContext>();
                bus.Subscribe<Number>((in Number message, in EventContext context) => exact.Add(context));
                bus.Subscribe<ICategory>(new ContextHandler<ICategory>((type, context) => category.Add(context)));
                bus.Prewarm<Number>(16);

                var before = Stopwatch.GetTimestamp();
                bus.Raise(new Number(7));
                var after = Stopwatch.GetTimestamp();
                Program.Equal(0, exact.Count);
                Program.Equal(1, bus.Pump());

                Program.Equal(Stopwatch.Frequency, EventContext.TimestampFrequency);
                Program.Equal(1, exact.Count);
                Program.Equal(1, category.Count);
                Program.Equal(1L, exact[0].Sequence);
                AssertBetween(before, exact[0].RaisedTimestamp, after);
                Program.Equal(exact[0].Sequence, category[0].Sequence);
                Program.Equal(exact[0].RaisedTimestamp, category[0].RaisedTimestamp);
            }
        }

        private static void Routing()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                var contexts = new List<EventContext>();
                var types = new List<Type>();
                bus.Subscribe<IEvent>(RecordingHandler<IEvent>("root", calls, contexts, types));
                bus.Subscribe<Number>((in Number message) => calls.Add("ordinary exact"));
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    calls.Add("context exact");
                    contexts.Add(context);
                    types.Add(typeof(Number));
                });
                bus.Subscribe<ICategory>(RecordingHandler<ICategory>("category", calls, contexts, types));
                bus.Subscribe<ILeft>(RecordingHandler<ILeft>("left", calls, contexts, types));
                bus.Subscribe<IRight>(RecordingHandler<IRight>("right", calls, contexts, types));
                bus.Subscribe<IDiamond>(RecordingHandler<IDiamond>("diamond", calls, contexts, types));
                bus.Subscribe<Number>(RecordingHandler<Number>("concrete category", calls, contexts, types));
                bus.Subscribe<IEvent>(new OrdinaryHandler(calls));

                bus.Raise(new Number(9));

                Program.Equal(1, bus.Pump());
                AssertSequence(calls,
                    "root", "ordinary exact", "context exact", "category", "left", "right",
                    "diamond", "concrete category", "ordinary category");
                Program.Equal(7, contexts.Count);
                for (var i = 0; i < contexts.Count; i++)
                {
                    Program.Equal(1L, contexts[i].Sequence);
                    Program.Equal(contexts[0].RaisedTimestamp, contexts[i].RaisedTimestamp);
                    Program.Equal(typeof(Number), types[i]);
                }
            }
        }

        private static ContextHandler<TBase> RecordingHandler<TBase>(
            string name, List<string> calls, List<EventContext> contexts, List<Type> types)
            where TBase : IEvent
        {
            return new ContextHandler<TBase>((type, context) =>
            {
                calls.Add(name);
                contexts.Add(context);
                types.Add(type);
            });
        }

        private static void ClassRouting()
        {
            using (var bus = new EventBus())
            {
                var types = new List<Type>();
                var contexts = new List<EventContext>();
                bus.Subscribe<BaseMessage>(new ContextHandler<BaseMessage>((type, context) =>
                {
                    types.Add(type);
                    contexts.Add(context);
                }));
                bus.Raise(new BaseMessage());
                bus.Raise(new DerivedMessage());

                Program.Equal(2, bus.Pump());
                AssertSequence(types, typeof(BaseMessage), typeof(DerivedMessage));
                Program.Equal(1L, contexts[0].Sequence);
                Program.Equal(2L, contexts[1].Sequence);
            }
        }

        private static void QueuePairing()
        {
            using (var bus = new EventBus())
            {
                var received = new List<(int Value, EventContext Context)>();
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                    received.Add((message.Value, context)));

                EnqueueNumbers(bus, 0, 300);
                Program.Equal(300, bus.Pump());
                for (var round = 0; round < 16; round++)
                {
                    EnqueueNumbers(bus, 300 + round * 71, 71);
                    Program.Equal(71, bus.Pump());
                }

                Program.Equal(1436, received.Count);
                for (var i = 0; i < received.Count; i++)
                {
                    Program.Equal(i, received[i].Value);
                    Program.Equal((long)i + 1, received[i].Context.Sequence);
                    if (i != 0)
                    {
                        AssertBetween(
                            received[i - 1].Context.RaisedTimestamp,
                            received[i].Context.RaisedTimestamp,
                            long.MaxValue);
                    }
                }

                Program.Equal(0, bus.Pump());
            }
        }

        private static void EnqueueNumbers(EventBus bus, int first, int count)
        {
            for (var i = 0; i < count; i++)
            {
                bus.Raise(new Number(first + i));
            }
        }

        private static void PublicationOrder()
        {
            using (var bus = new EventBus())
            {
                var sequences = new List<long>();
                bus.Subscribe<IEvent>(new ContextHandler<IEvent>((type, context) => sequences.Add(context.Sequence)));
                bus.Raise(new Number(10));
                bus.Raise(new OtherNumber(20));
                bus.Raise(new Number(11));
                bus.Raise(new OtherNumber(21));

                Program.Equal(4, bus.Pump());
                // Publication identity is global to a bus, while delivery still groups active channels.
                AssertSequence(sequences, 1L, 3L, 2L, 4L);
            }
        }

        private static void ReentrantQueueGrowth()
        {
            using (var bus = new EventBus())
            {
                var first = new List<(int Value, EventContext Context)>();
                var later = new List<(int Value, EventContext Context)>();
                var other = new List<(int Value, EventContext Context)>();
                var current = new List<(int Value, EventContext Context)>();
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    first.Add((message.Value, context));
                    if (message.Value == 1)
                    {
                        current.Add((message.Value, context));
                        // These raises resize the channel currently being drained and activate a new channel.
                        // The callback's readonly references must remain attached to its original publication.
                        EnqueueNumbers(bus, 3, 40);
                        bus.Raise(new OtherNumber(43));
                        current.Add((message.Value, context));
                    }
                });
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                    later.Add((message.Value, context)));
                bus.Subscribe<OtherNumber>((in OtherNumber message, in EventContext context) =>
                    other.Add((message.Value, context)));
                bus.Raise(new Number(1));
                bus.Raise(new Number(2));

                Program.Equal(2, bus.Pump());
                Program.Equal(2, first.Count);
                Program.Equal(2, later.Count);
                Program.Equal(0, other.Count);
                Program.Equal(2, current.Count);
                Program.Equal(1, current[0].Value);
                Program.Equal(current[0], current[1]);

                Program.Equal(41, bus.Pump());
                Program.Equal(42, first.Count);
                Program.Equal(42, later.Count);
                for (var i = 0; i < first.Count; i++)
                {
                    Program.Equal(i + 1, first[i].Value);
                    Program.Equal((long)i + 1, first[i].Context.Sequence);
                    Program.Equal(first[i], later[i]);
                }

                Program.Equal(1, other.Count);
                Program.Equal(43, other[0].Value);
                Program.Equal(43L, other[0].Context.Sequence);
            }
        }

        private static void ReentrantClassSnapshots()
        {
            using (var bus = new EventBus())
            {
                var message = new BaseMessage();
                var messages = new List<BaseMessage>();
                var contexts = new List<EventContext>();
                long reentrantBefore = 0;
                long reentrantAfter = 0;
                bus.Subscribe<BaseMessage>((in BaseMessage item, in EventContext context) =>
                {
                    messages.Add(item);
                    contexts.Add(context);
                    if (context.Sequence == 1)
                    {
                        reentrantBefore = Stopwatch.GetTimestamp();
                        bus.Raise(message);
                        reentrantAfter = Stopwatch.GetTimestamp();
                    }
                });

                var beforeFirst = Stopwatch.GetTimestamp();
                bus.Raise(message);
                var afterFirst = Stopwatch.GetTimestamp();
                bus.Raise(message);
                var afterSecond = Stopwatch.GetTimestamp();

                Program.Equal(2, bus.Pump());
                Program.Equal(2, contexts.Count);
                Program.Equal(1, bus.Pump());
                Program.Equal(3, contexts.Count);
                for (var i = 0; i < messages.Count; i++)
                {
                    Program.Equal(true, ReferenceEquals(message, messages[i]));
                    Program.Equal((long)i + 1, contexts[i].Sequence);
                }

                AssertBetween(beforeFirst, contexts[0].RaisedTimestamp, afterFirst);
                AssertBetween(afterFirst, contexts[1].RaisedTimestamp, afterSecond);
                AssertBetween(reentrantBefore, contexts[2].RaisedTimestamp, reentrantAfter);
            }
        }

        private static void SubscriptionTokens()
        {
            using (var bus = new EventBus())
            {
                var exact = new List<long>();
                var category = new List<long>();
                EventCallbackWithContext<Number> callback =
                    (in Number item, in EventContext context) => exact.Add(context.Sequence);
                var handler = new ContextHandler<ICategory>((type, context) => category.Add(context.Sequence));
                var first = bus.Subscribe(callback);
                var copy = first;
                var second = bus.Subscribe(callback);
                var firstCategory = bus.Subscribe<ICategory>(handler);
                var copiedCategory = firstCategory;
                var secondCategory = bus.Subscribe<ICategory>(handler);

                bus.Raise(new Number(0));
                bus.Pump();
                first.Dispose();
                copy.Dispose();
                firstCategory.Dispose();
                copiedCategory.Dispose();
                bus.Raise(new Number(0));
                bus.Pump();

                AssertSequence(exact, 1L, 1L, 2L);
                AssertSequence(category, 1L, 1L, 2L);
                second.Dispose();
                secondCategory.Dispose();
                bus.Raise(new Number(0));
                bus.Pump();
                Program.Equal(3, exact.Count);
                Program.Equal(3, category.Count);
            }
        }

        private static void ListenerMutation()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                Subscription category = default;
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    calls.Add("first " + context.Sequence);
                    if (context.Sequence == 1)
                    {
                        category.Dispose();
                        bus.Subscribe<Number>((in Number next, in EventContext nextContext) =>
                            calls.Add("new " + nextContext.Sequence));
                    }
                });

                bus.Raise(new Number(1));
                bus.Raise(new Number(2));
                // A subscriber installed after raising still participates in delivery.
                category = bus.Subscribe<ICategory>(new ContextHandler<ICategory>(
                    (type, context) => calls.Add("category " + context.Sequence)));
                Program.Equal(2, bus.Pump());
                AssertSequence(calls, "first 1", "category 1", "first 2", "new 2");
            }
        }

        private static void ExceptionIsolation()
        {
            var errors = new List<Exception>();
            using (var bus = new EventBus(errors.Add))
            {
                var sequences = new List<long>();
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                    throw new ApplicationException("context exact"));
                bus.Subscribe<ICategory>(new ContextHandler<ICategory>((type, context) =>
                    throw new ApplicationException("context category")));
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                    sequences.Add(context.Sequence));
                bus.Raise(new Number(1));
                bus.Raise(new Number(2));

                Program.Equal(2, bus.Pump());
                Program.Equal(4, errors.Count);
                AssertSequence(sequences, 1L, 2L);

                bus.ExceptionReporter = error => throw new ApplicationException("context reporter");
                bus.Raise(new Number(3));
                Program.Equal(1, bus.Pump());
                AssertSequence(sequences, 1L, 2L, 3L);
            }
        }

        private static void SelfUnsubscription()
        {
            using (var bus = new EventBus())
            {
                var exactCount = 0;
                var categoryCount = 0;
                Subscription exact = default;
                Subscription category = default;
                exact = bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    exactCount++;
                    exact.Dispose();
                });
                category = bus.Subscribe<ICategory>(new ContextHandler<ICategory>((type, context) =>
                {
                    categoryCount++;
                    category.Dispose();
                }));

                bus.Raise(new Number(1));
                bus.Raise(new Number(2));
                Program.Equal(2, bus.Pump());
                Program.Equal(1, exactCount);
                Program.Equal(1, categoryCount);
            }
        }

        private static void SequenceIsolation()
        {
            using (var first = new EventBus())
            {
                using (var second = new EventBus())
                {
                    var firstSequences = new List<long>();
                    var secondSequences = new List<long>();
                    first.Subscribe<IEvent>(new ContextHandler<IEvent>(
                        (type, context) => firstSequences.Add(context.Sequence)));
                    second.Subscribe<IEvent>(new ContextHandler<IEvent>(
                        (type, context) => secondSequences.Add(context.Sequence)));
                    first.Prewarm<Number>(128);
                    Throws<ArgumentNullException>(() => first.Raise<BaseMessage>(null));
                    BaseMessage erased = new DerivedMessage();
                    Throws<ArgumentException>(() => first.Raise(erased));
                    Throws<InvalidOperationException>(() => first.Raise(new MissingMetadata()));
                    ICategory category = new Number(1);
                    Throws<InvalidOperationException>(() => first.Raise(category));

                    first.Raise(new Number(1));
                    second.Raise(new OtherNumber(1));
                    first.Raise(new ThirdNumber(2));
                    second.Pump();
                    first.Pump();

                    AssertSequence(firstSequences, 1L, 2L);
                    AssertSequence(secondSequences, 1L);
                }
            }
        }

        private static void InvalidArguments()
        {
            using (var bus = new EventBus())
            {
                Throws<ArgumentNullException>(() =>
                    bus.Subscribe<Number>((EventCallbackWithContext<Number>)null));
                Throws<ArgumentNullException>(() =>
                    bus.Subscribe<ICategory>((IEventHandlerWithContext<ICategory>)null));

                bus.Dispose();
                Throws<ObjectDisposedException>(() =>
                    bus.Subscribe<Number>((in Number message, in EventContext context) =>
                    {
                    }));
                Throws<ObjectDisposedException>(() =>
                    bus.Subscribe<ICategory>(new ContextHandler<ICategory>((type, context) =>
                    {
                    })));
            }
        }

        private static void SequenceOverflow()
        {
            using (var bus = new EventBus())
            {
                var sequences = new List<long>();
                bus.Subscribe<Number>((in Number message, in EventContext context) => sequences.Add(context.Sequence));

                // Reach an otherwise impractical boundary without billions of publications or a test-only runtime API.
                var sequenceField = typeof(EventBus).GetField(
                    "_nextSequence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (sequenceField == null)
                {
                    throw new Exception("Could not locate the bus publication sequence for its overflow boundary test.");
                }

                sequenceField.SetValue(bus, long.MaxValue - 1);
                bus.Raise(new Number(1));
                Program.Equal(1, bus.Pump());
                AssertSequence(sequences, long.MaxValue);

                Throws<OverflowException>(() => bus.Raise(new OtherNumber(2)));
                Throws<OverflowException>(() => bus.Raise(new Number(3)));
                Program.Equal(0, bus.Pump());
                Program.Equal(long.MaxValue, (long)sequenceField.GetValue(bus));
            }
        }

        private static void ZeroAllocations(int mode)
        {
            using (var bus = new EventBus())
            {
                var handler = new CountingContextHandler();
                if (mode != 1)
                {
                    bus.Subscribe<Number>(Consume);
                }

                if (mode != 0)
                {
                    bus.Subscribe<ICategory>(handler);
                }

                bus.Prewarm<Number>(128);
                for (var round = 0; round < 100; round++)
                {
                    AllocationRound(bus);
                }

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var round = 0; round < 1000; round++)
                {
                    AllocationRound(bus);
                }

                Program.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
                if (mode != 0)
                {
                    Program.Equal(140800, handler.Count);
                    Program.Equal(140800L * 140801L / 2, handler.SequenceSum);
                }

                GC.KeepAlive(_sequenceSink);
                GC.KeepAlive(_timestampSink);
                GC.KeepAlive(_payloadSink);
            }
        }

        private static void AllocationRound(EventBus bus)
        {
            EnqueueNumbers(bus, 0, 128);
            bus.Pump();
        }

        private static void Consume(in Number message, in EventContext context)
        {
            _payloadSink += message.Value;
            _sequenceSink = context.Sequence;
            _timestampSink = context.RaisedTimestamp;
        }

        private static void ConstrainedCategoryZeroAllocations()
        {
            using (var bus = new EventBus())
            {
                var handler = new SummingContextHandler();
                bus.Subscribe<IValuedEvent>(handler);
                bus.Prewarm<ValuedEvent>(16);
                for (var i = 0; i < 100; i++)
                {
                    bus.Raise(new ValuedEvent(7));
                    bus.Pump();
                }

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10000; i++)
                {
                    bus.Raise(new ValuedEvent(7));
                    bus.Pump();
                }

                Program.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
                Program.Equal(70700, handler.Sum);
                Program.Equal(10100L * 10101L / 2, handler.SequenceSum);
            }
        }

        private static void AssertBetween(long minimum, long actual, long maximum)
        {
            if (actual < minimum || actual > maximum)
            {
                throw new Exception($"Expected timestamp between {minimum} and {maximum}; actual {actual}.");
            }
        }

        private static void AssertSequence<T>(IReadOnlyList<T> actual, params T[] expected)
        {
            Program.Equal(expected.Length, actual.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                Program.Equal(expected[i], actual[i]);
            }
        }

        private static void Throws<T>(Action action)
            where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new Exception("Expected exception " + typeof(T).Name + ".");
        }

        private sealed class ContextHandler<TBase> : IEventHandlerWithContext<TBase>
            where TBase : IEvent
        {
            private readonly Action<Type, EventContext> _record;

            /// <summary>Creates a recorder for context and retained concrete event type.</summary>
            /// <param name="record">The callback receiving the type and publication context.</param>
            public ContextHandler(Action<Type, EventContext> record)
            {
                _record = record;
            }

            /// <summary>Records one concrete event and its immutable publication context.</summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered payload passed by readonly reference.</param>
            /// <param name="context">The raise-time metadata passed by readonly reference.</param>
            public void Handle<T>(in T message, in EventContext context)
                where T : TBase
            {
                _record(typeof(T), context);
            }
        }

        private sealed class OrdinaryHandler : IEventHandler<IEvent>
        {
            private readonly List<string> _calls;

            /// <summary>Creates an ordinary handler for mixed callback ordering checks.</summary>
            /// <param name="calls">The list receiving delivery labels.</param>
            public OrdinaryHandler(List<string> calls)
            {
                _calls = calls;
            }

            /// <summary>Records the position of a callback that does not request metadata.</summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered payload passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : IEvent
            {
                _calls.Add("ordinary category");
            }
        }

        private sealed class CountingContextHandler : IEventHandlerWithContext<ICategory>
        {
            /// <summary>The number of delivered category messages.</summary>
            public int Count;

            /// <summary>The sum of publication identities observed by the handler.</summary>
            public long SequenceSum;

            /// <summary>Counts delivery and consumes metadata without boxing the payload.</summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered payload passed by readonly reference.</param>
            /// <param name="context">The raise-time metadata passed by readonly reference.</param>
            public void Handle<T>(in T message, in EventContext context)
                where T : ICategory
            {
                Count++;
                SequenceSum += context.Sequence;
                _timestampSink = context.RaisedTimestamp;
            }
        }

        private sealed class SummingContextHandler : IEventHandlerWithContext<IValuedEvent>
        {
            /// <summary>The sum of payload values accessed through the category constraint.</summary>
            public int Sum;

            /// <summary>The sum of publication identities observed by the handler.</summary>
            public long SequenceSum;

            /// <summary>Consumes a constrained payload property and context without boxing.</summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered payload passed by readonly reference.</param>
            /// <param name="context">The raise-time metadata passed by readonly reference.</param>
            public void Handle<T>(in T message, in EventContext context)
                where T : IValuedEvent
            {
                Sum += message.Value;
                SequenceSum += context.Sequence;
                _timestampSink = context.RaisedTimestamp;
            }
        }
    }
}
