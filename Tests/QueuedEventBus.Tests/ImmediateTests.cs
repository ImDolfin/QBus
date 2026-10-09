using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace QueuedEventBus.Tests
{
    internal static class ImmediateTests
    {
        internal static void Run(Action<string, Action> run)
        {
            run("immediate delivery preserves exact/category/diamond ordering and context", Routing);
            run("immediate class routing preserves concrete type and object identity", ClassRouting);
            run("immediate unobserved publications consume sequence without queuing", UnobservedPublications);
            run("immediate events overtake queues and share publication sequences", QueueOvertaking);
            run("immediate callbacks preserve every channel's pump snapshot", PumpSnapshot);
            run("nested immediate events capture changed subscribers independently", SubscriberSnapshots);
            run("immediate duplicate subscriptions and copied tokens stay independent", SubscriptionTokens);
            run("immediate dispatch rejects pump/disposal and recovers after exceptions", DispatchGuards);
            run("immediate listener and reporter failures preserve remaining callbacks", ExceptionIsolation);
            run("immediate nesting stops at 64 without consuming rejected sequences", () => NestingLimit(false));
            run("queued pumping does not consume an immediate nesting level", () => NestingLimit(true));
            run("invalid immediate publications leave sequence and queues untouched", InvalidPublications);
            run("immediate publication enforces owner thread and disposal", OwnershipAndDisposal);
            run("immediate sequence exhaustion preserves already queued messages", SequenceOverflow);
            run("immediate struct payload is stable when its caller variable changes", StructAliasing);
            run("immediate class reference is captured without cloning its object", ClassAliasing);
            run("zero warmed allocations: immediate exact struct delivery", () => ZeroAllocations(1));
            run("zero warmed allocations: immediate category struct delivery", () => ZeroAllocations(2));
            run("zero warmed allocations: immediate exact struct context delivery", () => ZeroAllocations(4));
            run("zero warmed allocations: immediate category struct context delivery", () => ZeroAllocations(8));
            run("zero warmed allocations: immediate combined struct delivery", () => ZeroAllocations(15));
            run("zero warmed allocations: nested immediate struct delivery", NestedZeroAllocations);
            run("zero warmed allocations: immediate reused class delivery", ClassZeroAllocations);
        }

        private static void Routing()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                var contexts = new List<EventContext>();
                var types = new List<Type>();
                bus.Subscribe<IEvent>(Recorder<IEvent>("root", calls, contexts, types));
                bus.Subscribe<Number>((in Number message) =>
                {
                    calls.Add("exact");
                });
                bus.Subscribe<ICategory>(Recorder<ICategory>("category", calls, contexts, types));
                bus.Subscribe<ILeft>(Recorder<ILeft>("left", calls, contexts, types));
                bus.Subscribe<IRight>(Recorder<IRight>("right", calls, contexts, types));
                bus.Subscribe<IDiamond>(Recorder<IDiamond>("diamond", calls, contexts, types));
                bus.Subscribe<Number>(Recorder<Number>("concrete category", calls, contexts, types));
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    calls.Add("context exact");
                    contexts.Add(context);
                    types.Add(typeof(Number));
                });
                bus.Subscribe<OtherNumber>((in OtherNumber message) =>
                {
                    calls.Add("wrong exact");
                });

                long before = Stopwatch.GetTimestamp();
                bus.RaiseImmediate(new Number(7));
                long after = Stopwatch.GetTimestamp();

                Sequence(calls, "root", "exact", "category", "left", "right", "diamond",
                    "concrete category", "context exact");
                Program.Equal(7, contexts.Count);
                for (var i = 0; i < contexts.Count; i++)
                {
                    Program.Equal(typeof(Number), types[i]);
                    Program.Equal(1L, contexts[i].Sequence);
                    Program.Equal(contexts[0].RaisedTimestamp, contexts[i].RaisedTimestamp);
                    AssertBetween(before, contexts[i].RaisedTimestamp, after);
                }

                Program.Equal(0, bus.Pump());
            }
        }

        private static void ClassRouting()
        {
            using (var bus = new EventBus())
            {
                var types = new List<Type>();
                var messages = new List<BaseMessage>();
                var baseMessage = new BaseMessage();
                var derivedMessage = new DerivedMessage();
                bus.Subscribe<BaseMessage>(new ContextHandler<BaseMessage>((type, context) =>
                {
                    types.Add(type);
                }));
                bus.Subscribe<BaseMessage>((in BaseMessage message) =>
                {
                    messages.Add(message);
                });
                bus.Subscribe<DerivedMessage>((in DerivedMessage message) =>
                {
                    messages.Add(message);
                });

                bus.RaiseImmediate(baseMessage);
                bus.RaiseImmediate(derivedMessage);

                Sequence(types, typeof(BaseMessage), typeof(DerivedMessage));
                Program.Equal(2, messages.Count);
                Program.Equal(true, ReferenceEquals(baseMessage, messages[0]));
                Program.Equal(true, ReferenceEquals(derivedMessage, messages[1]));
                Program.Equal(0, bus.Pump());
            }
        }

        private static void QueueOvertaking()
        {
            using (var bus = new EventBus())
            {
                var values = new List<int>();
                var sequences = new List<long>();
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    values.Add(message.Value);
                    sequences.Add(context.Sequence);
                    if (message.Value == 2)
                    {
                        bus.Raise(new Number(3));
                        bus.RaiseImmediate(new Number(4));
                    }
                });

                bus.Raise(new Number(1));
                bus.RaiseImmediate(new Number(2));
                Sequence(values, 2, 4);
                Sequence(sequences, 2L, 4L);

                Program.Equal(2, bus.Pump());
                Sequence(values, 2, 4, 1, 3);
                Sequence(sequences, 2L, 4L, 1L, 3L);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void UnobservedPublications()
        {
            using (var first = new EventBus())
            {
                using (var second = new EventBus())
                {
                    first.RaiseImmediate(new ThirdNumber(1));
                    Program.Equal(0, first.Pump());

                    long firstSequence = 0;
                    long secondSequence = 0;
                    first.Subscribe<ThirdNumber>((in ThirdNumber message, in EventContext context) =>
                    {
                        firstSequence = context.Sequence;
                    });
                    second.Subscribe<ThirdNumber>((in ThirdNumber message, in EventContext context) =>
                    {
                        secondSequence = context.Sequence;
                    });
                    first.RaiseImmediate(new ThirdNumber(2));
                    Program.Equal(2L, firstSequence);
                    Program.Equal(0L, secondSequence);
                    second.RaiseImmediate(new ThirdNumber(3));
                    Program.Equal(1L, secondSequence);
                    Program.Equal(0, first.Pump());
                    Program.Equal(0, second.Pump());
                }
            }
        }

        private static void PumpSnapshot()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                bus.Subscribe<IEvent>(new ContextHandler<IEvent>((type, context) =>
                {
                    if (type == typeof(ThirdNumber))
                    {
                        calls.Add("third");
                    }
                }));
                bus.Subscribe<Number>((in Number message) =>
                {
                    calls.Add("number " + message.Value);
                    if (message.Value == 1)
                    {
                        bus.RaiseImmediate(new OtherNumber(9));
                    }
                });
                bus.Subscribe<OtherNumber>((in OtherNumber message) =>
                {
                    calls.Add("other " + message.Value);
                    if (message.Value == 9)
                    {
                        // These affect the current queue, a later active queue, and a new queue.
                        bus.Raise(new Number(2));
                        bus.Raise(new OtherNumber(2));
                        bus.Raise(new ThirdNumber(3));
                    }
                });
                bus.Raise(new Number(1));
                bus.Raise(new OtherNumber(1));

                Program.Equal(2, bus.Pump());
                Sequence(calls, "number 1", "other 9", "other 1");
                Program.Equal(3, bus.Pump());
                Sequence(calls, "number 1", "other 9", "other 1", "number 2", "other 2", "third");
                Program.Equal(0, bus.Pump());
            }
        }

        private static void SubscriberSnapshots()
        {
            using (var bus = new EventBus())
            {
                var calls = new List<string>();
                Subscription first = default;
                Subscription old = default;
                first = bus.Subscribe<Number>((in Number message) =>
                {
                    calls.Add("first " + message.Value);
                    first.Dispose();
                    old.Dispose();
                    bus.Subscribe<Number>((in Number nested) =>
                    {
                        calls.Add("new " + nested.Value);
                    });
                    bus.RaiseImmediate(new Number(2));
                });
                old = bus.Subscribe<Number>((in Number message) =>
                {
                    calls.Add("old " + message.Value);
                });
                bus.Subscribe<ICategory>(new ContextHandler<ICategory>((type, context) =>
                {
                    calls.Add("category " + context.Sequence);
                }));

                bus.RaiseImmediate(new Number(1));
                Sequence(calls, "first 1", "category 2", "new 2", "old 1", "category 1");
                bus.RaiseImmediate(new Number(3));
                Sequence(calls, "first 1", "category 2", "new 2", "old 1", "category 1",
                    "category 3", "new 3");

                // Removing a copied token again must not remove a newer subscription.
                first.Dispose();
                old.Dispose();
                Program.Equal(0, bus.Pump());
            }
        }

        private static void DispatchGuards()
        {
            var errors = new List<Exception>();
            using (var bus = new EventBus(errors.Add))
            {
                int guardCount = 0;
                int otherCount = 0;
                bus.Subscribe<Number>((in Number message) =>
                {
                    guardCount += RejectDispatchOperation(() => bus.Pump());
                    guardCount += RejectDispatchOperation(bus.Dispose);
                    bus.Raise(new OtherNumber(1));
                    bus.RaiseImmediate(new OtherNumber(2));
                    throw new ApplicationException("listener failed after guarded operations");
                });
                bus.Subscribe<OtherNumber>((in OtherNumber message) =>
                {
                    otherCount++;
                });

                bus.RaiseImmediate(new Number(0));
                Program.Equal(2, guardCount);
                Program.Equal(1, otherCount);
                Program.Equal(1, errors.Count);
                Program.Equal(typeof(ApplicationException), errors[0].GetType());
                Program.Equal(1, bus.Pump());
                Program.Equal(2, otherCount);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void SubscriptionTokens()
        {
            using (var bus = new EventBus())
            {
                int exactCount = 0;
                int categoryCount = 0;
                EventCallback<Number> exact = (in Number message) =>
                {
                    exactCount++;
                };
                var category = new ContextHandler<ICategory>((type, context) =>
                {
                    categoryCount++;
                });
                var first = bus.Subscribe(exact);
                var firstCopy = first;
                var second = bus.Subscribe(exact);
                var firstCategory = bus.Subscribe<ICategory>(category);
                var categoryCopy = firstCategory;
                var secondCategory = bus.Subscribe<ICategory>(category);

                bus.RaiseImmediate(new Number(1));
                Program.Equal(2, exactCount);
                Program.Equal(2, categoryCount);
                first.Dispose();
                firstCopy.Dispose();
                firstCategory.Dispose();
                categoryCopy.Dispose();
                bus.RaiseImmediate(new Number(2));
                Program.Equal(3, exactCount);
                Program.Equal(3, categoryCount);
                second.Dispose();
                secondCategory.Dispose();
                bus.RaiseImmediate(new Number(3));
                Program.Equal(3, exactCount);
                Program.Equal(3, categoryCount);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void ExceptionIsolation()
        {
            var errors = new List<Exception>();
            using (var bus = new EventBus(errors.Add))
            {
                var sequences = new List<long>();
                bus.Subscribe<Number>((in Number message) =>
                {
                    throw new ApplicationException("exact listener");
                });
                bus.Subscribe<ICategory>(new ContextHandler<ICategory>((type, context) =>
                {
                    throw new ApplicationException("category listener");
                }));
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    sequences.Add(context.Sequence);
                });

                bus.RaiseImmediate(new Number(1));
                Program.Equal(2, errors.Count);
                Sequence(sequences, 1L);

                int reporterCalls = 0;
                bus.ExceptionReporter = error =>
                {
                    reporterCalls++;
                    throw new ApplicationException("reporter");
                };
                bus.RaiseImmediate(new Number(2));
                Program.Equal(2, reporterCalls);
                Sequence(sequences, 1L, 2L);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void NestingLimit(bool startWithPump)
        {
            var errors = new List<Exception>();
            using (var bus = new EventBus(errors.Add))
            {
                int numberCount = 0;
                int rejected = 0;
                int changedOuterContexts = 0;
                var sequences = new List<long>();
                bus.Subscribe<IEvent>(new ContextHandler<IEvent>((type, context) =>
                {
                    sequences.Add(context.Sequence);
                }));
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    numberCount++;
                    long outerSequence = context.Sequence;
                    if (message.Value < 64)
                    {
                        bus.RaiseImmediate(new Number(message.Value + 1));
                    }
                    else
                    {
                        rejected += RejectDispatchOperation(() => bus.RaiseImmediate(new Number(65)));
                        bus.Raise(new OtherNumber(0));
                    }

                    if (outerSequence != context.Sequence)
                    {
                        changedOuterContexts++;
                    }
                });

                int queuedOffset = startWithPump ? 1 : 0;
                if (startWithPump)
                {
                    bus.Raise(new Number(0));
                    Program.Equal(1, bus.Pump());
                }
                else
                {
                    bus.RaiseImmediate(new Number(1));
                }

                Program.Equal(64 + queuedOffset, numberCount);
                Program.Equal(1, rejected);
                Program.Equal(0, changedOuterContexts);
                Program.Equal(0, errors.Count);
                Program.Equal(64 + queuedOffset, sequences.Count);
                for (var i = 0; i < sequences.Count; i++)
                {
                    Program.Equal((long)i + 1, sequences[i]);
                }

                // A queued raise at maximum immediate depth still succeeds. Rejected immediate
                // raises consume neither a sequence nor a queue entry, and depth unwinds fully.
                bus.RaiseImmediate(new ThirdNumber(0));
                Program.Equal(66L + queuedOffset, sequences[sequences.Count - 1]);
                Program.Equal(1, bus.Pump());
                Program.Equal(65L + queuedOffset, sequences[sequences.Count - 1]);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void InvalidPublications()
        {
            using (var bus = new EventBus())
            {
                var sequences = new List<long>();
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    sequences.Add(context.Sequence);
                });
                bus.Raise(new Number(1));

                Throws<ArgumentNullException>(() => bus.RaiseImmediate<BaseMessage>(null));
                BaseMessage erasedClass = new DerivedMessage();
                Throws<ArgumentException>(() => bus.RaiseImmediate(erasedClass));
                IEvent erasedStruct = new Number(2);
                Throws<InvalidOperationException>(() => bus.RaiseImmediate(erasedStruct));
                Throws<InvalidOperationException>(() => bus.RaiseImmediate(new MissingMetadata()));
                Program.Equal(0, sequences.Count);

                bus.RaiseImmediate(new Number(3));
                Sequence(sequences, 2L);
                Program.Equal(1, bus.Pump());
                Sequence(sequences, 2L, 1L);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void OwnershipAndDisposal()
        {
            var bus = new EventBus();
            var sequences = new List<long>();
            var subscription = bus.Subscribe<Number>((in Number message, in EventContext context) =>
            {
                sequences.Add(context.Sequence);
            });
            Exception threadError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    bus.RaiseImmediate(new Number(1));
                }
                catch (Exception error)
                {
                    threadError = error;
                }
            });
            thread.Start();
            thread.Join();

            Program.Equal(typeof(InvalidOperationException), threadError?.GetType());
            bus.RaiseImmediate(new Number(2));
            Sequence(sequences, 1L);
            bus.Dispose();
            Throws<ObjectDisposedException>(() => bus.RaiseImmediate(new Number(3)));
            Sequence(sequences, 1L);
            subscription.Dispose();
            subscription.Dispose();
        }

        private static void SequenceOverflow()
        {
            using (var bus = new EventBus())
            {
                var sequences = new List<long>();
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    sequences.Add(context.Sequence);
                });
                bus.Raise(new Number(1));

                var sequenceField = typeof(EventBus).GetField(
                    "_nextSequence", BindingFlags.Instance | BindingFlags.NonPublic);
                if (sequenceField == null)
                {
                    throw new Exception("Could not locate the publication sequence for its overflow boundary test.");
                }

                sequenceField.SetValue(bus, long.MaxValue - 1);
                bus.RaiseImmediate(new Number(2));
                Sequence(sequences, long.MaxValue);
                Throws<OverflowException>(() => bus.RaiseImmediate(new Number(3)));
                Throws<OverflowException>(() => bus.RaiseImmediate(new ThirdNumber(4)));
                Program.Equal(long.MaxValue, (long)sequenceField.GetValue(bus));
                Program.Equal(1, bus.Pump());
                Sequence(sequences, long.MaxValue, 1L);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void StructAliasing()
        {
            using (var bus = new EventBus())
            {
                var current = new Number(1);
                var values = new List<int>();
                bus.Subscribe<Number>((in Number message) =>
                {
                    current = new Number(2);
                    values.Add(message.Value);
                });
                bus.Subscribe<Number>((in Number message) =>
                {
                    values.Add(message.Value);
                });

                // The in argument aliases a captured variable. A handler can legally reassign
                // that variable, but every callback must still see the original publication.
                bus.RaiseImmediate(in current);
                Sequence(values, 1, 1);
                Program.Equal(2, current.Value);
            }
        }

        private static void ClassAliasing()
        {
            using (var bus = new EventBus())
            {
                var original = new BaseMessage();
                var replacement = new BaseMessage();
                var current = original;
                var seen = new List<BaseMessage>();
                bus.Subscribe<BaseMessage>((in BaseMessage message) =>
                {
                    current = replacement;
                    seen.Add(message);
                });
                bus.Subscribe<BaseMessage>((in BaseMessage message) =>
                {
                    seen.Add(message);
                });

                bus.RaiseImmediate(in current);
                Program.Equal(2, seen.Count);
                Program.Equal(true, ReferenceEquals(original, seen[0]));
                Program.Equal(true, ReferenceEquals(original, seen[1]));
                Program.Equal(true, ReferenceEquals(replacement, current));
            }
        }

        private static void ZeroAllocations(int paths)
        {
            int errors = 0;
            using (var bus = new EventBus(error =>
            {
                errors++;
            }))
            {
                var counter = new AllocationCounter();
                SubscribeAllocationPaths(bus, counter, paths);
                bus.Prewarm<ValuedEvent>(1);
                for (var i = 0; i < 100; i++)
                {
                    bus.RaiseImmediate(new ValuedEvent(7));
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10000; i++)
                {
                    bus.RaiseImmediate(new ValuedEvent(7));
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Program.Equal(0L, allocated);
                Program.Equal(0, errors);
                AssertAllocationCounts(counter, paths, 10100);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void SubscribeAllocationPaths(EventBus bus, AllocationCounter counter, int paths)
        {
            if ((paths & 1) != 0)
            {
                bus.Subscribe<ValuedEvent>(counter.Exact);
            }

            if ((paths & 2) != 0)
            {
                bus.Subscribe<IValuedEvent>((IEventHandler<IValuedEvent>)counter);
            }

            if ((paths & 4) != 0)
            {
                bus.Subscribe<ValuedEvent>(counter.ExactContext);
            }

            if ((paths & 8) != 0)
            {
                bus.Subscribe<IValuedEvent>((IEventHandlerWithContext<IValuedEvent>)counter);
            }
        }

        private static void AssertAllocationCounts(AllocationCounter counter, int paths, int publications)
        {
            Program.Equal((paths & 1) != 0 ? publications : 0, counter.ExactCount);
            Program.Equal((paths & 2) != 0 ? publications : 0, counter.CategoryCount);
            Program.Equal((paths & 4) != 0 ? publications : 0, counter.ExactContextCount);
            Program.Equal((paths & 8) != 0 ? publications : 0, counter.CategoryContextCount);
            int listeners = ((paths & 1) != 0 ? 1 : 0) + ((paths & 2) != 0 ? 1 : 0)
                + ((paths & 4) != 0 ? 1 : 0) + ((paths & 8) != 0 ? 1 : 0);
            int contextListeners = ((paths & 4) != 0 ? 1 : 0) + ((paths & 8) != 0 ? 1 : 0);
            Program.Equal(publications * listeners * 7, counter.PayloadSum);
            Program.Equal((long)publications * (publications + 1) / 2 * contextListeners, counter.SequenceSum);
        }

        private static void NestedZeroAllocations()
        {
            int errors = 0;
            using (var bus = new EventBus(error =>
            {
                errors++;
            }))
            {
                int parentCount = 0;
                long parentSequenceSum = 0;
                var counter = new AllocationCounter();
                SubscribeAllocationPaths(bus, counter, 15);
                bus.Subscribe<Number>((in Number message, in EventContext context) =>
                {
                    parentCount++;
                    parentSequenceSum += context.Sequence;
                    bus.RaiseImmediate(new ValuedEvent(7));
                });
                bus.Prewarm<Number>(1);
                bus.Prewarm<ValuedEvent>(1);
                for (var i = 0; i < 100; i++)
                {
                    bus.RaiseImmediate(new Number(1));
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10000; i++)
                {
                    bus.RaiseImmediate(new Number(1));
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Program.Equal(0L, allocated);
                Program.Equal(0, errors);
                Program.Equal(10100, parentCount);
                Program.Equal(10100L * 10100L, parentSequenceSum);
                Program.Equal(10100, counter.ExactCount);
                Program.Equal(10100, counter.CategoryCount);
                Program.Equal(10100, counter.ExactContextCount);
                Program.Equal(10100, counter.CategoryContextCount);
                Program.Equal(10100 * 4 * 7, counter.PayloadSum);
                Program.Equal(2L * 10100 * 10101, counter.SequenceSum);
                Program.Equal(0, bus.Pump());
            }
        }

        private static void ClassZeroAllocations()
        {
            using (var bus = new EventBus())
            {
                int count = 0;
                long sequenceSum = 0;
                var message = new BaseMessage();
                bus.Subscribe<BaseMessage>(new ContextHandler<BaseMessage>((type, context) =>
                {
                    count++;
                    sequenceSum += context.Sequence;
                }));
                bus.Prewarm<BaseMessage>(1);
                for (var i = 0; i < 100; i++)
                {
                    bus.RaiseImmediate(message);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10000; i++)
                {
                    bus.RaiseImmediate(message);
                }

                Program.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
                Program.Equal(10100, count);
                Program.Equal(10100L * 10101 / 2, sequenceSum);
                Program.Equal(0, bus.Pump());
            }
        }

        private static ContextHandler<TBase> Recorder<TBase>(
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

        private static int RejectDispatchOperation(Action operation)
        {
            try
            {
                operation();
            }
            catch (InvalidOperationException)
            {
                return 1;
            }

            return 0;
        }

        private static void AssertBetween(long minimum, long value, long maximum)
        {
            if (value < minimum || value > maximum)
            {
                throw new Exception($"Expected timestamp between {minimum} and {maximum}; actual {value}.");
            }
        }

        private static void Sequence<T>(IReadOnlyList<T> actual, params T[] expected)
        {
            Program.Equal(expected.Length, actual.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                Program.Equal(expected[i], actual[i]);
            }
        }

        private static void Throws<T>(Action action) where T : Exception
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

        private sealed class ContextHandler<TBase> : IEventHandlerWithContext<TBase> where TBase : IEvent
        {
            private readonly Action<Type, EventContext> _record;

            internal ContextHandler(Action<Type, EventContext> record)
            {
                _record = record;
            }

            /// <summary>Records the concrete delivered event type and publication metadata.</summary>
            /// <typeparam name="T">The concrete event type.</typeparam>
            /// <param name="message">The event payload.</param>
            /// <param name="context">The publication metadata.</param>
            public void Handle<T>(in T message, in EventContext context) where T : TBase
            {
                _record(typeof(T), context);
            }
        }

        private sealed class AllocationCounter : IEventHandler<IValuedEvent>, IEventHandlerWithContext<IValuedEvent>
        {
            internal int ExactCount;
            internal int CategoryCount;
            internal int ExactContextCount;
            internal int CategoryContextCount;
            internal int PayloadSum;
            internal long SequenceSum;

            internal void Exact(in ValuedEvent message)
            {
                ExactCount++;
                PayloadSum += message.Value;
            }

            internal void ExactContext(in ValuedEvent message, in EventContext context)
            {
                ExactContextCount++;
                PayloadSum += message.Value;
                SequenceSum += context.Sequence;
            }

            /// <summary>Reads a category property through its concrete generic constraint.</summary>
            /// <typeparam name="T">The concrete event type.</typeparam>
            /// <param name="message">The payload whose value contributes to the delivery count.</param>
            public void Handle<T>(in T message) where T : IValuedEvent
            {
                CategoryCount++;
                PayloadSum += message.Value;
            }

            /// <summary>Reads a constrained category property and its publication metadata.</summary>
            /// <typeparam name="T">The concrete event type.</typeparam>
            /// <param name="message">The event payload.</param>
            /// <param name="context">The publication metadata.</param>
            public void Handle<T>(in T message, in EventContext context) where T : IValuedEvent
            {
                CategoryContextCount++;
                PayloadSum += message.Value;
                SequenceSum += context.Sequence;
            }
        }
    }
}
