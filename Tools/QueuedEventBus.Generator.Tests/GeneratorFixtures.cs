internal static class GeneratorFixtures
{
    internal const string Contract = """
        namespace QueuedEventBus
        {
            /// <summary>Marks a queued notification.</summary>
            public interface IEvent
            {
            }

            /// <summary>Receives an exact event.</summary>
            /// <typeparam name="T">The event type.</typeparam>
            /// <param name="message">The delivered event.</param>
            public delegate void EventCallback<T>(in T message) where T : IEvent;

            /// <summary>Represents metadata attached to a single publication.</summary>
            public readonly struct EventContext
            {
            }

            /// <summary>Receives an exact event and its publication metadata.</summary>
            /// <typeparam name="T">The event type.</typeparam>
            /// <param name="message">The delivered event.</param>
            /// <param name="context">The publication metadata.</param>
            public delegate void EventCallbackWithContext<T>(in T message, in EventContext context) where T : IEvent;

            /// <summary>Receives events belonging to a category.</summary>
            /// <typeparam name="TBase">The category type.</typeparam>
            public interface IEventHandler<TBase> where TBase : IEvent
            {
                /// <summary>Receives the event without erasing its concrete type.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="message">The delivered event.</param>
                void Handle<T>(in T message) where T : TBase;
            }

            /// <summary>Receives category events and their publication metadata.</summary>
            /// <typeparam name="TBase">The category type.</typeparam>
            public interface IEventHandlerWithContext<TBase> where TBase : IEvent
            {
                /// <summary>Receives the event and metadata without erasing its concrete type.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="message">The delivered event.</param>
                /// <param name="context">The publication metadata.</param>
                void Handle<T>(in T message, in EventContext context) where T : TBase;
            }

            /// <summary>Provides the instance API recognized by the analyzer.</summary>
            public sealed class EventBus
            {
                /// <summary>Queues a concrete event.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="message">The event to enqueue.</param>
                public void Raise<T>(in T message) where T : IEvent
                {
                }

                /// <summary>Delivers a concrete event synchronously.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="message">The event to deliver.</param>
                public void RaiseImmediate<T>(in T message) where T : IEvent
                {
                }

                /// <summary>Reserves queue capacity.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="capacity">The desired capacity.</param>
                public void Prewarm<T>(int capacity) where T : IEvent
                {
                }

                /// <summary>Subscribes an exact callback.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="callback">The event callback.</param>
                public void Subscribe<T>(EventCallback<T> callback) where T : IEvent
                {
                }

                /// <summary>Subscribes a category handler.</summary>
                /// <typeparam name="T">The category type.</typeparam>
                /// <param name="handler">The category handler.</param>
                public void Subscribe<T>(IEventHandler<T> handler) where T : IEvent
                {
                }

                /// <summary>Subscribes an exact callback that receives publication metadata.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="callback">The event callback.</param>
                public void Subscribe<T>(EventCallbackWithContext<T> callback) where T : IEvent
                {
                }

                /// <summary>Subscribes a category handler that receives publication metadata.</summary>
                /// <typeparam name="T">The category type.</typeparam>
                /// <param name="handler">The category handler.</param>
                public void Subscribe<T>(IEventHandlerWithContext<T> handler) where T : IEvent
                {
                }
            }

            /// <summary>Provides the static API recognized by the analyzer.</summary>
            public static class Events
            {
                /// <summary>Queues a concrete event.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="message">The event to enqueue.</param>
                public static void Raise<T>(in T message) where T : IEvent
                {
                }

                /// <summary>Delivers a concrete event synchronously.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="message">The event to deliver.</param>
                public static void RaiseImmediate<T>(in T message) where T : IEvent
                {
                }

                /// <summary>Reserves queue capacity.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="capacity">The desired capacity.</param>
                public static void Prewarm<T>(int capacity) where T : IEvent
                {
                }

                /// <summary>Subscribes an exact callback.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="callback">The event callback.</param>
                public static void Subscribe<T>(EventCallback<T> callback) where T : IEvent
                {
                }

                /// <summary>Subscribes a category handler.</summary>
                /// <typeparam name="T">The category type.</typeparam>
                /// <param name="handler">The category handler.</param>
                public static void Subscribe<T>(IEventHandler<T> handler) where T : IEvent
                {
                }

                /// <summary>Subscribes an exact callback that receives publication metadata.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="callback">The event callback.</param>
                public static void Subscribe<T>(EventCallbackWithContext<T> callback) where T : IEvent
                {
                }

                /// <summary>Subscribes a category handler that receives publication metadata.</summary>
                /// <typeparam name="T">The category type.</typeparam>
                /// <param name="handler">The category handler.</param>
                public static void Subscribe<T>(IEventHandlerWithContext<T> handler) where T : IEvent
                {
                }
            }
        }

        namespace QueuedEventBus.Generated
        {
            /// <summary>Stands in for a strongly typed delivery route.</summary>
            /// <typeparam name="T">The concrete event type.</typeparam>
            public sealed class GeneratedRoute<T> where T : QueuedEventBus.IEvent
            {
            }

            /// <summary>Provides the route factory targeted by generated registrations.</summary>
            public static class GeneratedRoutes
            {
                /// <summary>Creates a placeholder route for compilation-only tests.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <typeparam name="TBase">The category type.</typeparam>
                /// <returns>A null placeholder that is never dispatched.</returns>
                public static GeneratedRoute<T> Create<T, TBase>()
                    where T : TBase
                    where TBase : QueuedEventBus.IEvent
                {
                    return null;
                }
            }

            /// <summary>Provides registration signatures targeted by generated source.</summary>
            public static class GeneratedEventCatalog
            {
                /// <summary>Accepts routes for a value-type event.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="routes">The generated category routes.</param>
                public static void RegisterStruct<T>(params GeneratedRoute<T>[] routes)
                    where T : struct, QueuedEventBus.IEvent
                {
                }

                /// <summary>Accepts routes for a reference-type event.</summary>
                /// <typeparam name="T">The concrete event type.</typeparam>
                /// <param name="routes">The generated category routes.</param>
                public static void RegisterClass<T>(params GeneratedRoute<T>[] routes)
                    where T : class, QueuedEventBus.IEvent
                {
                }
            }
        }

        namespace UnityEngine
        {
            /// <summary>Contains the Unity startup stage used by generated source.</summary>
            public enum RuntimeInitializeLoadType
            {
                /// <summary>Runs after all application assemblies are loaded.</summary>
                AfterAssembliesLoaded
            }

            /// <summary>Stands in for Unity's runtime startup hook attribute.</summary>
            public sealed class RuntimeInitializeOnLoadMethodAttribute : System.Attribute
            {
                /// <summary>Creates the startup hook attribute.</summary>
                /// <param name="type">The desired startup stage.</param>
                public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type)
                {
                }
            }
        }

        namespace UnityEngine.Scripting
        {
            /// <summary>Stands in for Unity's member preservation attribute.</summary>
            public sealed class PreserveAttribute : System.Attribute
            {
            }

            /// <summary>Stands in for Unity's assembly preservation attribute.</summary>
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class AlwaysLinkAssemblyAttribute : System.Attribute
            {
            }
        }

        namespace UnityEditor
        {
            /// <summary>Stands in for Unity's editor startup hook attribute.</summary>
            public sealed class InitializeOnLoadMethodAttribute : System.Attribute
            {
            }
        }
        """;

    internal const string ValidRoutes = """
        /// <summary>Provides the common root of the diamond.</summary>
        public interface ICommon : QueuedEventBus.IEvent
        {
        }

        /// <summary>Provides the left branch of the diamond.</summary>
        public interface ILeft : ICommon
        {
        }

        /// <summary>Provides the right branch of the diamond.</summary>
        public interface IRight : ICommon
        {
        }

        /// <summary>Uses both branches of the diamond.</summary>
        public readonly struct Damage : ILeft, IRight
        {
        }

        /// <summary>Provides an abstract category.</summary>
        public abstract class AbstractEvent : QueuedEventBus.IEvent
        {
        }

        /// <summary>Provides a concrete event that is also a category.</summary>
        public class BaseEvent : AbstractEvent
        {
        }

        /// <summary>Combines class inheritance and an interface category.</summary>
        public sealed class Child : BaseEvent, IRight
        {
        }

        internal class Container
        {
            internal struct Nested : ICommon
            {
            }
        }

        /// <summary>Provides a generic category closed by a concrete event.</summary>
        /// <typeparam name="T">The category argument.</typeparam>
        public interface IGeneric<T> : QueuedEventBus.IEvent
        {
        }

        /// <summary>Uses a closed generic category.</summary>
        public struct ClosedGenericCategory : IGeneric<int>
        {
        }
        """;

    internal const string UnsupportedDeclarations = """
        /// <summary>Exercises unsupported generic events.</summary>
        /// <typeparam name="T">The open event argument.</typeparam>
        public struct Generic<T> : QueuedEventBus.IEvent
        {
        }

        /// <summary>Exercises unsupported generic containing types.</summary>
        /// <typeparam name="T">The open container argument.</typeparam>
        public class GenericContainer<T>
        {
            /// <summary>Depends on the containing type's open argument.</summary>
            public struct Nested : QueuedEventBus.IEvent
            {
            }
        }

        /// <summary>Contains event declarations inaccessible to the registrar.</summary>
        public class Container
        {
            private struct Private : QueuedEventBus.IEvent
            {
            }

            /// <summary>Requires a derived container to access it.</summary>
            protected class Protected : QueuedEventBus.IEvent
            {
            }

            private class Hidden
            {
                /// <summary>Is inaccessible because its containing type is private.</summary>
                public struct HiddenEvent : QueuedEventBus.IEvent
                {
                }
            }
        }

        /// <summary>Cannot be stored in a managed queue.</summary>
        public ref struct RefEvent : QueuedEventBus.IEvent
        {
        }
        """;

    internal const string InaccessibleCategories = """
        /// <summary>Provides a category whose argument can be inaccessible.</summary>
        /// <typeparam name="T">The category argument.</typeparam>
        public interface IGeneric<T> : QueuedEventBus.IEvent
        {
        }

        /// <summary>Contains visible events with hidden category components.</summary>
        public class Outer
        {
            private interface IHidden : QueuedEventBus.IEvent
            {
            }

            private class HiddenArgument
            {
            }

            /// <summary>Implements a private category.</summary>
            public struct Visible : IHidden
            {
            }

            /// <summary>Uses a private type argument in a public category.</summary>
            public struct VisibleGeneric : IGeneric<HiddenArgument>
            {
            }
        }
        """;

    internal const string InvalidUsage = """
        /// <summary>Provides a concrete event base class.</summary>
        public class BaseEvent : QueuedEventBus.IEvent
        {
        }

        /// <summary>Provides a derived event whose publication type can be erased.</summary>
        public class Derived : BaseEvent
        {
        }

        /// <summary>Provides a category that cannot own a concrete queue.</summary>
        public abstract class Abstract : QueuedEventBus.IEvent
        {
        }

        /// <summary>Contains calls expected to produce usage diagnostics.</summary>
        public class Uses
        {
            private static void Receive(in QueuedEventBus.IEvent message)
            {
            }

            /// <summary>Exercises erased publications and invalid exact subscriptions.</summary>
            /// <param name="bus">The bus being analyzed.</param>
            /// <param name="erased">An event with its concrete type erased.</param>
            /// <param name="abstractEvent">An event exposed as an abstract base.</param>
            public void Test(QueuedEventBus.EventBus bus, QueuedEventBus.IEvent erased, Abstract abstractEvent)
            {
                bus.Raise(erased);
                QueuedEventBus.Events.Raise(abstractEvent);
                bus.Prewarm<QueuedEventBus.IEvent>(8);
                bus.Subscribe<QueuedEventBus.IEvent>(Receive);
                bus.Raise<BaseEvent>(new Derived());
                QueuedEventBus.Events.Raise<BaseEvent>((BaseEvent)new Derived());
            }
        }
        """;

    internal const string ValidUsage = """
        /// <summary>Provides a concrete notification.</summary>
        public struct Damage : QueuedEventBus.IEvent
        {
        }

        /// <summary>Contains valid exact and category subscriptions.</summary>
        public class Uses : QueuedEventBus.IEventHandler<QueuedEventBus.IEvent>
        {
            /// <inheritdoc />
            public void Handle<T>(in T message) where T : QueuedEventBus.IEvent
            {
            }

            private static void Receive(in Damage message)
            {
            }

            /// <summary>Exercises supported subscription and publication calls.</summary>
            /// <param name="bus">The bus being analyzed.</param>
            public void Test(QueuedEventBus.EventBus bus)
            {
                bus.Subscribe<QueuedEventBus.IEvent>(this);
                QueuedEventBus.Events.Subscribe<QueuedEventBus.IEvent>(this);
                bus.Raise(new Damage());
                bus.RaiseImmediate(new Damage());
                QueuedEventBus.Events.RaiseImmediate(new Damage());
                bus.Prewarm<Damage>(8);
                bus.Subscribe<Damage>(Receive);
            }

            /// <summary>Forwards publication without erasing its generic type.</summary>
            /// <typeparam name="T">The event type retained by forwarding.</typeparam>
            /// <param name="bus">The receiving bus.</param>
            /// <param name="message">The event to forward.</param>
            public void Forward<T>(QueuedEventBus.EventBus bus, in T message) where T : QueuedEventBus.IEvent
            {
                bus.Raise(in message);
                bus.RaiseImmediate(in message);
                QueuedEventBus.Events.RaiseImmediate(in message);
            }
        }
        """;

    internal const string RuntimeIntegration = """
        /// <summary>Provides the common root of the routing diamond.</summary>
        public interface ICategory : QueuedEventBus.IEvent
        {
        }

        /// <summary>Provides the left diamond branch.</summary>
        public interface ILeft : ICategory
        {
        }

        /// <summary>Provides the right diamond branch.</summary>
        public interface IRight : ICategory
        {
        }

        /// <summary>Exercises struct routing through the diamond.</summary>
        public struct Damage : ILeft, IRight
        {
        }

        /// <summary>Provides a concrete event base class.</summary>
        public class BaseEvent : ICategory
        {
        }

        /// <summary>Exercises class category routing.</summary>
        public sealed class Child : BaseEvent
        {
        }

        /// <summary>Counts deliveries while verifying each concrete generic type.</summary>
        public sealed class Handler : QueuedEventBus.IEventHandler<ICategory>
        {
            /// <summary>Counts struct notifications received.</summary>
            public int Structs;

            /// <summary>Counts class notifications received.</summary>
            public int Classes;

            /// <inheritdoc />
            public void Handle<T>(in T message) where T : ICategory
            {
                if (typeof(T) == typeof(Damage))
                {
                    Structs++;
                }
                else if (typeof(T) == typeof(Child))
                {
                    Classes++;
                }
                else
                {
                    throw new System.InvalidOperationException("Unexpected concrete event type");
                }
            }
        }

        /// <summary>Exercises generated registrations against the actual runtime.</summary>
        public static class Integration
        {
            /// <summary>Verifies exact delivery, category delivery, and erased-type rejection.</summary>
            public static void Run()
            {
                using (var bus = new QueuedEventBus.EventBus(exception =>
                {
                    throw exception;
                }))
                {
                    var handler = new Handler();
                    int exact = 0;
                    bus.Subscribe<ICategory>(handler);
                    bus.Subscribe<Damage>((in Damage value) =>
                    {
                        exact++;
                    });
                    bus.Prewarm<Damage>(32);

                    bus.Raise(new Damage());
                    bus.Raise(new Child());
                    if (bus.Pump() != 2 || exact != 1 || handler.Structs != 1 || handler.Classes != 1)
                    {
                        throw new System.Exception("Generated routes did not deliver exactly once with concrete types");
                    }

                    AssertErasedPublicationRejected(bus);
                }
            }

            private static void AssertErasedPublicationRejected(QueuedEventBus.EventBus bus)
            {
                bool rejected = false;
                try
                {
                    bus.Raise<BaseEvent>(new Child());
                }
                catch (System.ArgumentException)
                {
                    rejected = true;
                }

                if (!rejected)
                {
                    throw new System.Exception("Generated class descriptor failed exact type validation");
                }
            }
        }
        """;
}
