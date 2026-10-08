internal static class MetadataFixtures
{
    internal const string InvalidUsage = """
        /// <summary>Provides an abstract event category.</summary>
        public abstract class AbstractEvent : QueuedEventBus.IEvent
        {
        }

        /// <summary>Contains metadata subscriptions that require category handlers.</summary>
        public static class Uses
        {
            /// <summary>Exercises unsupported exact interface and abstract subscriptions.</summary>
            /// <param name="bus">The bus being analyzed.</param>
            public static void Test(QueuedEventBus.EventBus bus)
            {
                bus.Subscribe<QueuedEventBus.IEvent>(Receive);
                QueuedEventBus.Events.Subscribe<QueuedEventBus.IEvent>(Receive);
                bus.Subscribe<AbstractEvent>(ReceiveAbstract);
                QueuedEventBus.Events.Subscribe<AbstractEvent>(ReceiveAbstract);
            }

            private static void Receive(in QueuedEventBus.IEvent message, in QueuedEventBus.EventContext context)
            {
            }

            private static void ReceiveAbstract(in AbstractEvent message, in QueuedEventBus.EventContext context)
            {
            }
        }
        """;

    internal const string ValidUsage = """
        /// <summary>Provides a concrete event.</summary>
        public readonly struct Damage : QueuedEventBus.IEvent
        {
        }

        /// <summary>Receives category publications with metadata.</summary>
        public sealed class Handler : QueuedEventBus.IEventHandlerWithContext<QueuedEventBus.IEvent>
        {
            /// <inheritdoc />
            public void Handle<T>(in T message, in QueuedEventBus.EventContext context)
                where T : QueuedEventBus.IEvent
            {
            }
        }

        /// <summary>Contains valid context subscription overloads and generic forwarding.</summary>
        public static class Uses
        {
            /// <summary>Subscribes exact and category listeners through each API.</summary>
            /// <param name="bus">The bus being analyzed.</param>
            /// <param name="handler">The category listener.</param>
            public static void Test(QueuedEventBus.EventBus bus, Handler handler)
            {
                bus.Subscribe<QueuedEventBus.IEvent>(handler);
                QueuedEventBus.Events.Subscribe<QueuedEventBus.IEvent>(handler);
                bus.Subscribe<Damage>(Receive);
                QueuedEventBus.Events.Subscribe<Damage>(Receive);
            }

            /// <summary>Forwards a typed exact context callback without erasing the event type.</summary>
            /// <typeparam name="T">The event type retained by forwarding.</typeparam>
            /// <param name="bus">The receiving bus.</param>
            /// <param name="callback">The exact callback.</param>
            public static void Forward<T>(
                QueuedEventBus.EventBus bus,
                QueuedEventBus.EventCallbackWithContext<T> callback) where T : QueuedEventBus.IEvent
            {
                bus.Subscribe<T>(callback);
            }

            private static void Receive(in Damage message, in QueuedEventBus.EventContext context)
            {
            }
        }
        """;

    internal const string Shared = """
        namespace MetadataShared
        {
            /// <summary>Exposes a value through a category declared in another assembly.</summary>
            public interface IValueEvent : QueuedEventBus.IEvent
            {
                /// <summary>Gets the value carried by the concrete notification.</summary>
                /// <value>The notification value.</value>
                int Value { get; }
            }

            /// <summary>Captures metadata and constrained values without knowing concrete events.</summary>
            public sealed class Handler : QueuedEventBus.IEventHandlerWithContext<IValueEvent>
            {
                /// <summary>Contains every publication context in delivery order.</summary>
                public readonly System.Collections.Generic.List<QueuedEventBus.EventContext> Contexts =
                    new System.Collections.Generic.List<QueuedEventBus.EventContext>();

                /// <summary>Contains the concrete types observed by generic delivery.</summary>
                public readonly System.Collections.Generic.List<System.Type> Types =
                    new System.Collections.Generic.List<System.Type>();

                /// <summary>Contains the sum read through the constrained category property.</summary>
                public int Total;

                /// <inheritdoc />
                public void Handle<T>(in T message, in QueuedEventBus.EventContext context) where T : IValueEvent
                {
                    Contexts.Add(context);
                    Types.Add(typeof(T));
                    Total += message.Value;
                }
            }
        }
        """;

    internal const string Events = """
        namespace MetadataEvents
        {
            /// <summary>Provides a value-type notification in the event assembly.</summary>
            public readonly struct Damage : MetadataShared.IValueEvent
            {
                /// <summary>Creates a notification carrying a value.</summary>
                /// <param name="value">The notification value.</param>
                public Damage(int value)
                {
                    Value = value;
                }

                /// <inheritdoc />
                public int Value { get; }
            }

            /// <summary>Provides a reference notification that can be raised more than once.</summary>
            public sealed class Repeated : MetadataShared.IValueEvent
            {
                /// <summary>Creates a notification carrying a value.</summary>
                /// <param name="value">The notification value.</param>
                public Repeated(int value)
                {
                    Value = value;
                }

                /// <inheritdoc />
                public int Value { get; }
            }
        }
        """;

    internal const string Consumer = """
        /// <summary>Verifies context delivery across distinct category, event, and consumer assemblies.</summary>
        public static class MetadataIntegration
        {
            /// <summary>Executes generated exact and category routing against the actual runtime.</summary>
            public static void Run()
            {
                int errors = 0;
                using (var bus = new QueuedEventBus.EventBus(exception =>
                {
                    errors++;
                }))
                {
                    var handler = new MetadataShared.Handler();
                    var exact = new System.Collections.Generic.List<QueuedEventBus.EventContext>();
                    bus.Subscribe<MetadataShared.IValueEvent>(handler);
                    bus.Subscribe<MetadataEvents.Damage>((in MetadataEvents.Damage message, in QueuedEventBus.EventContext context) =>
                    {
                        exact.Add(context);
                    });

                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    RaiseInterleaved(bus);
                    long raised = System.Diagnostics.Stopwatch.GetTimestamp();

                    if (bus.Pump() != 4 || errors != 0 || handler.Total != 17 || exact.Count != 2)
                    {
                        throw new System.Exception("Cross-assembly context subscriptions did not receive all events");
                    }

                    VerifyCategoryContexts(handler, started, raised);
                    VerifyExactContexts(handler, exact);
                }
            }

            private static void RaiseInterleaved(QueuedEventBus.EventBus bus)
            {
                var repeated = new MetadataEvents.Repeated(5);
                bus.Raise(new MetadataEvents.Damage(3));
                bus.Raise(repeated);
                bus.Raise(new MetadataEvents.Damage(4));
                bus.Raise(repeated);
            }

            private static void VerifyCategoryContexts(MetadataShared.Handler handler, long started, long raised)
            {
                long[] sequences = { 1, 3, 2, 4 };
                if (handler.Contexts.Count != sequences.Length ||
                    QueuedEventBus.EventContext.TimestampFrequency != System.Diagnostics.Stopwatch.Frequency)
                {
                    throw new System.Exception("Publication context count or timestamp frequency was invalid");
                }

                for (int index = 0; index < sequences.Length; index++)
                {
                    var context = handler.Contexts[index];
                    var expectedType = index < 2 ? typeof(MetadataEvents.Damage) : typeof(MetadataEvents.Repeated);
                    if (context.Sequence != sequences[index] ||
                        context.RaisedTimestamp < started || context.RaisedTimestamp > raised ||
                        handler.Types[index] != expectedType)
                    {
                        throw new System.Exception("Generated category delivery lost publication metadata or concrete type");
                    }
                }
            }

            private static void VerifyExactContexts(
                MetadataShared.Handler handler,
                System.Collections.Generic.List<QueuedEventBus.EventContext> exact)
            {
                for (int index = 0; index < exact.Count; index++)
                {
                    if (exact[index].Sequence != handler.Contexts[index].Sequence ||
                        exact[index].RaisedTimestamp != handler.Contexts[index].RaisedTimestamp)
                    {
                        throw new System.Exception("Exact and category callbacks received different publication contexts");
                    }
                }
            }
        }
        """;
}
