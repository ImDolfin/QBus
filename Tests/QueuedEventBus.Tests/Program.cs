using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace QueuedEventBus.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static int _allocationSink;

        /// <summary>
        /// Runs the runtime correctness suite or the requested benchmark matrix.
        /// </summary>
        /// <param name="args">
        /// Command-line arguments; use <c>--benchmark</c> or <c>--benchmark-context</c> to measure throughput.
        /// </param>
        /// <returns>Zero when the selected run succeeds; one when a correctness test fails.</returns>
        public static int Main(string[] args)
        {
            FixtureRegistration.Register();

            if (args.Length != 0 && (args[0] == "--benchmark" || args[0] == "--benchmark-context"))
            {
                return Benchmarks.Run(args[0] == "--benchmark-context");
            }

            RunCorrectnessTests();
            RunAllocationTests();
            MetadataTests.Run(Run);
            ImmediateTests.Run(Run);

            Console.WriteLine($"RESULT {_passed} passed, {_failed} failed");
            GC.KeepAlive(_allocationSink);
            return _failed == 0 ? 0 : 1;
        }

        private static void RunCorrectnessTests()
        {
            Run("empty pump and FIFO through growth and wraparound", QueueGrowthAndWraparound);
            Run("exact, category, concrete-category and diamond routing", Routing);
            Run("class base category includes concrete base and derived messages", ClassRouting);
            Run("snapshot captures all channels before any callback", AllChannelSnapshot);
            Run("new channels raised during callbacks wait until next pump", NewChannelSnapshot);
            Run("channel activation order and per-channel FIFO", ActivationOrder);
            Run("subscribers are selected at delivery time", DeliveryTimeSubscriptions);
            Run("listener mutation affects next event, not current callbacks", ListenerMutation);
            Run("self-unsubscription affects subsequent events", SelfUnsubscription);
            Run("duplicate subscription and copied tokens are independent/idempotent", SubscriptionTokens);
            Run("listener and exception-reporter failures preserve delivery", ExceptionIsolation);
            Run("recursive pump and disposal during callbacks are rejected", DispatchGuards);
            Run("independent buses isolate subscriptions and queues", IsolatedBuses);
            Run("invalid publication and subscription arguments", InvalidArguments);
            Run("use after disposal is rejected and tokens remain safe", DisposedBus);
            Run("operations enforce owner thread", OwnerThread);
            Run("dequeued payload references can be collected", ClearedPayloadReferences);
            Run("disposed bus releases pending payload references", DisposedPayloadReferences);
        }

        private static void RunAllocationTests()
        {
            Run("zero warmed allocations: exact struct delivery", () => ZeroAllocations(0));
            Run("zero warmed allocations: category struct delivery", () => ZeroAllocations(1));
            Run("zero warmed allocations: combined struct delivery", () => ZeroAllocations(2));
            Run("zero warmed allocations: constrained category payload property", ConstrainedCategoryZeroAllocations);
            Run("zero warmed allocations: class publication and delivery", ClassZeroAllocations);
            Run("zero warmed allocations: reentrant scheduling", ReentrantZeroAllocations);
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + Environment.NewLine + error);
            }
        }

        private static void QueueGrowthAndWraparound()
        {
            using (var bus = new EventBus())
            {
                Equal(0, bus.Pump());
                var received = new List<int>();
                bus.Subscribe<Number>((in Number item) => received.Add(item.Value));
                bus.Prewarm<Number>(32);

                for (var i = 0; i < 1000; i++)
                {
                    bus.Raise(new Number(i));
                }

                Equal(1000, bus.Pump());
                for (var round = 0; round < 16; round++)
                {
                    for (var i = 0; i < 71; i++)
                    {
                        bus.Raise(new Number(1000 + round * 71 + i));
                    }

                    Equal(71, bus.Pump());
                }

                Equal(2136, received.Count);
                for (var i = 0; i < received.Count; i++)
                {
                    Equal(i, received[i]);
                }

                Equal(0, bus.Pump());
            }
        }

        private static void Routing()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                bus.Subscribe<IEvent>(new RecordingHandler<IEvent>(calls, "root"));
                bus.Subscribe<Number>((in Number item) => calls.Add("exact"));
                bus.Subscribe<ICategory>(new RecordingHandler<ICategory>(calls, "category"));
                bus.Subscribe<ILeft>(new RecordingHandler<ILeft>(calls, "left"));
                bus.Subscribe<IRight>(new RecordingHandler<IRight>(calls, "right"));
                bus.Subscribe<IDiamond>(new RecordingHandler<IDiamond>(calls, "diamond"));
                bus.Subscribe<Number>(new RecordingHandler<Number>(calls, "concrete category"));
                bus.Subscribe<OtherNumber>((in OtherNumber item) => calls.Add("wrong exact"));

                bus.Raise(new Number(1));

                Equal(1, bus.Pump());
                Sequence(calls, "root", "exact", "category", "left", "right", "diamond", "concrete category");
            }
        }

        private static void ClassRouting()
        {
            using (var bus = new EventBus())
            {
                var seen = new List<Type>();
                var exactBase = 0;
                var exactDerived = 0;
                bus.Subscribe<BaseMessage>(new TypeHandler<BaseMessage>(seen));
                bus.Subscribe<BaseMessage>((in BaseMessage item) => exactBase++);
                bus.Subscribe<DerivedMessage>((in DerivedMessage item) => exactDerived++);

                bus.Raise(new BaseMessage());
                bus.Raise(new DerivedMessage());

                Equal(2, bus.Pump());
                Sequence(seen, typeof(BaseMessage), typeof(DerivedMessage));
                Equal(1, exactBase);
                Equal(1, exactDerived);
            }
        }

        private static void AllChannelSnapshot()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                bus.Subscribe<Number>((in Number item) =>
                {
                    calls.Add("number " + item.Value);
                    if (item.Value == 1)
                    {
                        bus.Raise(new Number(2));
                        bus.Raise(new OtherNumber(2));
                    }
                });
                bus.Subscribe<OtherNumber>((in OtherNumber item) => calls.Add("other " + item.Value));

                bus.Raise(new Number(1));
                bus.Raise(new OtherNumber(1));

                Equal(2, bus.Pump());
                // Every active channel must have its count frozen before the first callback runs.
                Sequence(calls, "number 1", "other 1");
                Equal(2, bus.Pump());
                Sequence(calls, "number 1", "other 1", "number 2", "other 2");
                Equal(0, bus.Pump());
            }
        }

        private static void NewChannelSnapshot()
        {
            using (var bus = new EventBus())
            {
                var handler = new CountingHandler<IEvent>();
                bus.Subscribe<IEvent>(handler);
                bus.Subscribe<Number>((in Number item) => bus.Raise(new ThirdNumber(1)));

                bus.Raise(new Number(1));

                Equal(1, bus.Pump());
                Equal(1, handler.Count);
                Equal(1, bus.Pump());
                Equal(2, handler.Count);
                Equal(0, bus.Pump());
            }
        }

        private static void ActivationOrder()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<int>();
                bus.Subscribe<Number>((in Number item) => calls.Add(item.Value));
                bus.Subscribe<OtherNumber>((in OtherNumber item) => calls.Add(item.Value));

                bus.Raise(new OtherNumber(10));
                bus.Raise(new Number(20));
                bus.Raise(new OtherNumber(11));
                bus.Raise(new Number(21));

                Equal(4, bus.Pump());
                Sequence(calls, 10, 11, 20, 21);
                calls.Clear();
                bus.Raise(new Number(30));
                bus.Raise(new OtherNumber(40));
                bus.Pump();
                Sequence(calls, 30, 40);
            }
        }

        private static void DeliveryTimeSubscriptions()
        {
            using (var bus = new EventBus())
            {
                var count = 0;
                bus.Raise(new Number(1));
                var subscription = bus.Subscribe<Number>((in Number item) => count++);
                Equal(1, bus.Pump());
                Equal(1, count);
                bus.Raise(new Number(2));
                subscription.Dispose();
                Equal(1, bus.Pump());
                Equal(1, count);
            }
        }

        private static void ListenerMutation()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                Subscription second = default;
                bus.Subscribe<Number>((in Number item) =>
                {
                    calls.Add("first " + item.Value);
                    if (item.Value == 1)
                    {
                        second.Dispose();
                        bus.Subscribe<Number>((in Number next) => calls.Add("new " + next.Value));
                    }
                });
                second = bus.Subscribe<Number>((in Number item) => calls.Add("second " + item.Value));
                bus.Raise(new Number(1));
                bus.Raise(new Number(2));
                bus.Pump();
                // The removed listener finishes the first event; the added listener starts with the second.
                Sequence(calls, "first 1", "second 1", "first 2", "new 2");
            }
        }

        private static void SelfUnsubscription()
        {
            using (var bus = new EventBus())
            {
                var count = 0;
                Subscription token = default;
                token = bus.Subscribe<Number>((in Number item) =>
                {
                    count++;
                    token.Dispose();
                });
                bus.Raise(new Number(1));
                bus.Raise(new Number(2));

                Equal(2, bus.Pump());
                Equal(1, count);
            }
        }

        private static void SubscriptionTokens()
        {
            using (var bus = new EventBus())
            {
                var count = 0;
                EventCallback<Number> callback = (in Number item) => count++;
                var first = bus.Subscribe(callback);
                var copied = first;
                var second = bus.Subscribe(callback);
                bus.Raise(new Number(0));
                bus.Pump();
                Equal(2, count);
                first.Dispose();
                copied.Dispose();
                bus.Raise(new Number(0));
                bus.Pump();
                Equal(3, count);
                second.Dispose();
                default(Subscription).Dispose();
                bus.Raise(new Number(0));
                bus.Pump();
                Equal(3, count);
            }
        }

        private static void ExceptionIsolation()
        {
            var errors = new List<Exception>();
            using (var bus = new EventBus(errors.Add))
            {
                var count = 0;
                bus.Subscribe<Number>((in Number item) => throw new ApplicationException("listener"));
                bus.Subscribe<Number>((in Number item) => count++);

                bus.Raise(new Number(1));
                bus.Raise(new Number(2));

                Equal(2, bus.Pump());
                Equal(2, errors.Count);
                Equal(2, count);
                bus.ExceptionReporter = error => throw new ApplicationException("reporter");
                bus.Raise(new Number(3));

                Equal(1, bus.Pump());
                Equal(3, count);
                bus.ExceptionReporter = null;
                bus.Raise(new Number(4));

                Equal(1, bus.Pump());
                Equal(4, count);
            }
        }

        private static void DispatchGuards()
        {
            using (var bus = new EventBus())
            {
                var count = 0;
                bus.Subscribe<Number>((in Number item) =>
                {
                    Throws<InvalidOperationException>(() => bus.Pump());
                    Throws<InvalidOperationException>(() => bus.Dispose());
                    count++;
                });
                bus.Raise(new Number(0));

                Equal(1, bus.Pump());
                Equal(1, count);
                bus.Raise(new Number(1));

                Equal(1, bus.Pump());
                Equal(2, count);
            }
        }

        private static void IsolatedBuses()
        {
            using (var first = new EventBus())
            {
                using (var second = new EventBus())
                {
                    var a = 0;
                    var b = 0;
                    first.Subscribe<Number>((in Number item) => a++);
                    second.Subscribe<Number>((in Number item) => b++);
                    first.Raise(new Number(1));
                    Equal(0, second.Pump());
                    Equal(0, a);
                    Equal(0, b);
                    Equal(1, first.Pump());
                    Equal(1, a);
                    Equal(0, b);
                    second.Raise(new Number(1));
                    first.Dispose();
                    Equal(1, second.Pump());
                    Equal(1, b);
                }
            }
        }

        private static void InvalidArguments()
        {
            using (var bus = new EventBus())
            {
                Throws<ArgumentNullException>(() => bus.Subscribe<Number>((EventCallback<Number>)null));
                Throws<ArgumentNullException>(() => bus.Subscribe<ICategory>((IEventHandler<ICategory>)null));
                Throws<ArgumentNullException>(() => bus.Raise<BaseMessage>(null));
                BaseMessage erased = new DerivedMessage();
                Throws<ArgumentException>(() => bus.Raise(erased));
                ICategory category = new Number(1);
                Throws<InvalidOperationException>(() => bus.Raise(category));
                Throws<InvalidOperationException>(() => bus.Raise(new MissingMetadata()));
                Throws<InvalidOperationException>(() => bus.Prewarm<AbstractMessage>(16));
                Throws<ArgumentOutOfRangeException>(() => bus.Prewarm<Number>(-1));
                Equal(0, bus.Pump());
                bus.Raise(new Number(1));

                Equal(1, bus.Pump());
            }
        }

        private static void DisposedBus()
        {
            var bus = new EventBus();
            var token = bus.Subscribe<Number>((in Number item) =>
            {
            });
            bus.Dispose();
            bus.Dispose();
            token.Dispose();
            Throws<ObjectDisposedException>(() => bus.Raise(new Number(1)));
            Throws<ObjectDisposedException>(() => bus.Prewarm<Number>(16));
            Throws<ObjectDisposedException>(() => bus.Subscribe<Number>((in Number item) =>
            {
            }));
            Throws<ObjectDisposedException>(() => bus.Subscribe<ICategory>(new CountingHandler<ICategory>()));
            Throws<ObjectDisposedException>(() => bus.Pump());
        }

        private static void OwnerThread()
        {
            using (var bus = new EventBus())
            {
                var token = bus.Subscribe<Number>((in Number item) =>
                {
                });
                var failures = new List<Exception>();
                var thread = new Thread(() =>
                {
                    foreach (var operation in new Action[]
                    {
                        () => bus.Raise(new Number(1)),
                        () => bus.Prewarm<Number>(16),
                        () => bus.Subscribe<Number>((in Number item) =>
                        {
                        }),
                        () => bus.Subscribe<ICategory>(new CountingHandler<ICategory>()),
                        () => bus.Pump(),
                        () => bus.Dispose(),
                        () => token.Dispose()
                    })
                    {
                        try
                        {
                            Throws<InvalidOperationException>(operation);
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);
                        }
                    }
                });
                thread.Start();
                thread.Join();
                Equal(0, failures.Count);
                Equal(0, bus.Pump());
                token.Dispose();
            }
        }

        private static void ClearedPayloadReferences()
        {
            using (var bus = new EventBus())
            {
                var reference = EnqueueReference(bus);
                Equal(1, bus.Pump());
                ForceCollection();
                Equal(false, reference.IsAlive);
                // Keep the bus alive so collection proves its queue released the payload.
                GC.KeepAlive(bus);
            }
        }

        private static void DisposedPayloadReferences()
        {
            var bus = new EventBus();
            var reference = EnqueueReference(bus);
            bus.Dispose();
            ForceCollection();
            Equal(false, reference.IsAlive);
            // Keep the bus alive so collection proves its queue released the payload.
            GC.KeepAlive(bus);
        }

        // A separate non-inlined frame prevents the JIT from keeping a local payload
        // alive while the caller checks whether the queue released its reference.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference EnqueueReference(EventBus bus)
        {
            var payload = new object();
            var reference = new WeakReference(payload);
            bus.Raise(new ReferenceEvent { Reference = payload });
            return reference;
        }

        private static void ForceCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static void ZeroAllocations(int mode)
        {
            using (var bus = new EventBus())
            {
                if (mode != 1)
                {
                    bus.Subscribe<Number>(ConsumeNumber);
                }

                var handler = new CountingHandler<ICategory>();
                if (mode != 0)
                {
                    bus.Subscribe<ICategory>(handler);
                }

                bus.Prewarm<Number>(128);

                for (var i = 0; i < 100; i++)
                {
                    AllocationRound(bus);
                }

                // Warmup above includes queue allocation and generic callback initialization.
                // Keep assertions and diagnostic formatting outside the measured interval.
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++)
                {
                    AllocationRound(bus);
                }

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Equal(0L, allocated);
                if (mode != 0)
                {
                    Equal(140800, handler.Count);
                }
            }
        }

        private static void AllocationRound(EventBus bus)
        {
            for (var j = 0; j < 128; j++)
            {
                bus.Raise(new Number(j));
            }

            bus.Pump();
        }

        private static void ConsumeNumber(in Number message)
        {
            _allocationSink += message.Value;
        }

        private static void ConstrainedCategoryZeroAllocations()
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

                // Warmup above includes queue allocation and generic callback initialization.
                // Keep assertions and diagnostic formatting outside the measured interval.
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10000; i++)
                {
                    bus.Raise(new ValuedEvent(7));
                    bus.Pump();
                }

                Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
                Equal(70700, handler.Sum);
            }
        }

        private static void ClassZeroAllocations()
        {
            using (var bus = new EventBus())
            {
                var handler = new CountingHandler<IEvent>();
                bus.Subscribe<IEvent>(handler);
                bus.Prewarm<BaseMessage>(1);
                var message = new BaseMessage();
                for (var i = 0; i < 100; i++)
                {
                    bus.Raise(message);
                    bus.Pump();
                }

                // Warmup above includes queue allocation and generic callback initialization.
                // Keep assertions and diagnostic formatting outside the measured interval.
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10000; i++)
                {
                    bus.Raise(message);
                    bus.Pump();
                }

                Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
                Equal(10100, handler.Count);
            }
        }

        private static void ReentrantZeroAllocations()
        {
            int reportedExceptions = 0;
            int numberDeliveries = 0;
            int otherDeliveries = 0;

            using (var bus = new EventBus(error =>
            {
                reportedExceptions++;
            }))
            {
                bus.Prewarm<Number>(2);
                bus.Prewarm<OtherNumber>(2);
                var numberToken = bus.Subscribe<Number>((in Number item) =>
                {
                    numberDeliveries++;
                    bus.Raise(new OtherNumber(item.Value));
                });
                var otherToken = bus.Subscribe<OtherNumber>((in OtherNumber item) =>
                {
                    otherDeliveries++;
                    bus.Raise(new Number(item.Value));
                });

                bus.Raise(new Number(1));
                for (var i = 0; i < 100; i++)
                {
                    bus.Pump();
                }

                Equal(50, numberDeliveries);
                Equal(50, otherDeliveries);
                Equal(0, reportedExceptions);

                AssertReentrantPumpAllocations(bus);
                Equal(5050, numberDeliveries);
                Equal(5050, otherDeliveries);
                Equal(0, reportedExceptions);

                // Removing both producers leaves exactly one queued event, then an empty bus.
                numberToken.Dispose();
                otherToken.Dispose();
                Equal(1, bus.Pump());
                Equal(0, bus.Pump());
                Equal(5050, numberDeliveries);
                Equal(5050, otherDeliveries);
                Equal(0, reportedExceptions);
            }
        }

        private static void AssertReentrantPumpAllocations(EventBus bus)
        {
            int delivered = 0;
            int unexpectedPumpCounts = 0;

            // Count real work so a stopped chain cannot pass by measuring empty pumps.
            // Assertions and diagnostic formatting remain outside the measured interval.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++)
            {
                int count = bus.Pump();
                delivered += count;
                if (count != 1)
                {
                    unexpectedPumpCounts++;
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Equal(0L, allocated);
            Equal(10000, delivered);
            Equal(0, unexpectedPumpCounts);
        }

        internal static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception($"Expected {expected}; actual {actual}.");
            }
        }

        private static void Sequence<T>(IReadOnlyList<T> actual, params T[] expected)
        {
            Equal(expected.Length, actual.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                Equal(expected[i], actual[i]);
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

        private sealed class RecordingHandler<TBase> : IEventHandler<TBase> where TBase : IEvent
        {
            private readonly List<string> _calls;
            private readonly string _name;

            /// <summary>
            /// Creates a named callback recorder.
            /// </summary>
            /// <param name="calls">The list receiving delivery records.</param>
            /// <param name="name">The label appended for each delivery.</param>
            public RecordingHandler(List<string> calls, string name)
            {
                _calls = calls;
                _name = name;
            }

            /// <summary>
            /// Appends this handler's label for a delivered event.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered event passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : TBase
            {
                _calls.Add(_name);
            }
        }

        private sealed class TypeHandler<TBase> : IEventHandler<TBase> where TBase : IEvent
        {
            private readonly List<Type> _calls;

            /// <summary>
            /// Creates a recorder for concrete delivered event types.
            /// </summary>
            /// <param name="calls">The list receiving concrete event types.</param>
            public TypeHandler(List<Type> calls)
            {
                _calls = calls;
            }

            /// <summary>
            /// Records the concrete type retained by generic category delivery.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered event passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : TBase
            {
                _calls.Add(typeof(T));
            }
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
}
