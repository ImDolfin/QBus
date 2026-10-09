internal static class ImmediateFixtures
{
    internal const string InvalidUsage = """
        /// <summary>Provides a concrete event base class.</summary>
        public class BaseEvent : QueuedEventBus.IEvent
        {
        }

        /// <summary>Provides an event whose concrete publication type must be retained.</summary>
        public sealed class Derived : BaseEvent
        {
        }

        /// <summary>Provides a category that cannot be published directly.</summary>
        public abstract class AbstractEvent : QueuedEventBus.IEvent
        {
        }

        /// <summary>Exercises immediate publication diagnostics on both APIs.</summary>
        public static class Uses
        {
            /// <summary>Attempts interface, abstract, and erased concrete publication.</summary>
            /// <param name="bus">The owned event bus.</param>
            /// <param name="erased">An event with an interface static type.</param>
            /// <param name="abstractEvent">An event with an abstract static type.</param>
            public static void Test(
                QueuedEventBus.EventBus bus,
                QueuedEventBus.IEvent erased,
                AbstractEvent abstractEvent)
            {
                bus.RaiseImmediate(erased);
                QueuedEventBus.Events.RaiseImmediate(erased);
                bus.RaiseImmediate(abstractEvent);
                QueuedEventBus.Events.RaiseImmediate(abstractEvent);
                bus.RaiseImmediate<BaseEvent>(new Derived());
                QueuedEventBus.Events.RaiseImmediate<BaseEvent>(new Derived());
                bus.RaiseImmediate((BaseEvent)new Derived());
                QueuedEventBus.Events.RaiseImmediate((BaseEvent)new Derived());
            }
        }
        """;

    internal const string Consumer = """
        /// <summary>Verifies generated immediate routes across category, event, and consumer assemblies.</summary>
        public static class ImmediateIntegration
        {
            /// <summary>Checks synchronous delivery, preserved concrete types, and shared publication metadata.</summary>
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
                    bus.Raise(new MetadataEvents.Damage(3));
                    bus.RaiseImmediate(new MetadataEvents.Repeated(5));
                    bus.RaiseImmediate(new MetadataEvents.Damage(4));
                    long raised = System.Diagnostics.Stopwatch.GetTimestamp();

                    if (errors != 0 || handler.Total != 9 || handler.Contexts.Count != 2 || exact.Count != 1)
                    {
                        throw new System.Exception("Generated immediate routes did not complete synchronously");
                    }

                    if (bus.Pump() != 1 || bus.Pump() != 0 || errors != 0 || handler.Total != 12 || exact.Count != 2)
                    {
                        throw new System.Exception("Immediate publication changed the queued publication");
                    }

                    VerifyCategoryContexts(handler, started, raised);
                    VerifyExactContexts(handler, exact);
                }
            }

            private static void VerifyCategoryContexts(MetadataShared.Handler handler, long started, long raised)
            {
                long[] sequences = { 2, 3, 1 };
                if (handler.Contexts.Count != sequences.Length)
                {
                    throw new System.Exception("Cross-assembly immediate category routing lost a publication");
                }

                for (int index = 0; index < sequences.Length; index++)
                {
                    var context = handler.Contexts[index];
                    var expectedType = index == 0 ? typeof(MetadataEvents.Repeated) : typeof(MetadataEvents.Damage);
                    if (context.Sequence != sequences[index] ||
                        context.RaisedTimestamp < started || context.RaisedTimestamp > raised ||
                        handler.Types[index] != expectedType)
                    {
                        throw new System.Exception("Immediate routing lost the publication context or concrete type");
                    }
                }
            }

            private static void VerifyExactContexts(
                MetadataShared.Handler handler,
                System.Collections.Generic.List<QueuedEventBus.EventContext> exact)
            {
                for (int index = 0; index < exact.Count; index++)
                {
                    var categoryContext = handler.Contexts[index + 1];
                    if (exact[index].Sequence != categoryContext.Sequence ||
                        exact[index].RaisedTimestamp != categoryContext.RaisedTimestamp)
                    {
                        throw new System.Exception("Immediate exact and category routes received different contexts");
                    }
                }
            }
        }
        """;
}
