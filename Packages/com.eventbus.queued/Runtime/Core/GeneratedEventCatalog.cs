using System;
using System.ComponentModel;
using System.Threading;

namespace QueuedEventBus
{
    internal static class EventTypeIds
    {
        private static int _next = -1;

        internal static int Next()
        {
            return Interlocked.Increment(ref _next);
        }
    }

    internal static class EventTypeId<T> where T : IEvent
    {
        // IDs describe types, not buses. Keep them stable while metadata survives Play-session resets.
        internal static readonly int Value = EventTypeIds.Next();
    }

    internal static class EventMetadata<T> where T : IEvent
    {
        internal static EventDescriptor<T> Descriptor;

        internal static EventDescriptor<T> Require()
        {
            if (Descriptor != null)
            {
                return Descriptor;
            }

            throw new InvalidOperationException(
                "No generated event metadata for " + typeof(T).FullName +
                ". Use a concrete supported IEvent type and ensure the QueuedEventBus generator is enabled. " +
                "In Unity, use the bus after generated initialization (Awake or later).");
        }
    }

    internal abstract class EventDescriptor<T> where T : IEvent
    {
        // Descriptors retain immutable route factories, never a bus instance or subscriber.
        internal readonly Generated.GeneratedRoute<T>[] Routes;

        protected EventDescriptor(Generated.GeneratedRoute<T>[] routes)
        {
            Routes = routes;
        }

        internal abstract void Validate(in T message);
    }

    internal sealed class StructDescriptor<T> : EventDescriptor<T> where T : struct, IEvent
    {
        internal StructDescriptor(Generated.GeneratedRoute<T>[] routes) : base(routes)
        {
        }

        internal override void Validate(in T message)
        {
            // A struct's declared type is already exact. Avoid an object/interface conversion here.
        }
    }

    internal sealed class ClassDescriptor<T> : EventDescriptor<T> where T : class, IEvent
    {
        internal ClassDescriptor(Generated.GeneratedRoute<T>[] routes) : base(routes)
        {
        }

        internal override void Validate(in T message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            // Routing is generated for T. Accepting a derived instance here would silently omit
            // its more-specific routes, so reject type erasure before touching a queue.
            if (message.GetType() != typeof(T))
            {
                throw new ArgumentException(
                    "Publish class events with their exact concrete type, not a base-typed variable.",
                    nameof(message));
            }
        }
    }
}

namespace QueuedEventBus.Generated
{
    /// <summary>
    /// Describes a generated category route for one concrete event type.
    /// </summary>
    /// <typeparam name="T">The concrete event type whose payload is preserved by the route.</typeparam>
    /// <remarks>
    /// This type is public so generated code in consumer assemblies can register routes.
    /// Application code normally uses <see cref="EventBus"/> instead of this infrastructure.
    /// Each bus binds the shared descriptor to its own category subscriptions.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class GeneratedRoute<T> where T : IEvent
    {
        internal GeneratedRoute()
        {
        }

        internal abstract Type CategoryType { get; }

        internal abstract BoundRoute<T> Bind(EventBus bus, Channel<T> channel);
    }

    internal sealed class CategoryRoute<TEvent, TCategory> : GeneratedRoute<TEvent>
        where TEvent : TCategory
        where TCategory : IEvent
    {
        internal override Type CategoryType
        {
            get
            {
                return typeof(TCategory);
            }
        }

        internal override BoundRoute<TEvent> Bind(EventBus bus, Channel<TEvent> channel)
        {
            var bucket = bus.GetOrCreateCategory<TCategory>();

            // Reverse dependencies let subscription changes rebuild only affected live channels.
            bucket.Dependents.Add(channel);

            return new CategoryBoundRoute<TEvent, TCategory>(bucket);
        }
    }

    /// <summary>
    /// Creates the constrained route descriptors referenced by generated registration code.
    /// </summary>
    /// <remarks>
    /// The generator emits closed event/category pairs so the runtime does not need reflection
    /// invocation or runtime generic construction to discover dispatch adapters.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class GeneratedRoutes
    {
        /// <summary>
        /// Creates a route that preserves a concrete payload type while invoking a category handler.
        /// </summary>
        /// <typeparam name="TEvent">The concrete event type implementing or inheriting the category.</typeparam>
        /// <typeparam name="TCategory">The event interface, base class, or concrete category type.</typeparam>
        /// <returns>A new route descriptor for generated metadata registration.</returns>
        /// <remarks>
        /// This setup operation allocates a descriptor. It does not allocate a payload queue or
        /// register a subscriber, and it is not part of the warmed publication/delivery path.
        /// </remarks>
        public static GeneratedRoute<TEvent> Create<TEvent, TCategory>()
            where TEvent : TCategory
            where TCategory : IEvent
        {
            return new CategoryRoute<TEvent, TCategory>();
        }
    }

    /// <summary>
    /// Registers immutable event metadata emitted by the bundled source generator.
    /// </summary>
    /// <remarks>
    /// Registration is idempotent per concrete event type. The first descriptor is retained;
    /// subsequent calls leave it unchanged, including when entering Play mode without domain reload.
    /// The Unity-generated initialization callbacks perform registration before normal bus use.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class GeneratedEventCatalog
    {
        /// <summary>
        /// Registers the generated category routes for a concrete struct event.
        /// </summary>
        /// <typeparam name="T">The concrete struct event type.</typeparam>
        /// <param name="routes">The unique, non-null generated routes to retain for this type.</param>
        /// <remarks>
        /// On first registration, the array is validated and copied so later caller mutations
        /// cannot change the descriptor. Later registration calls are ignored.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The initial <paramref name="routes"/> array is null.</exception>
        /// <exception cref="ArgumentException">The initial array contains a null route or duplicate category.</exception>
        public static void RegisterStruct<T>(params GeneratedRoute<T>[] routes) where T : struct, IEvent
        {
            if (EventMetadata<T>.Descriptor != null)
            {
                return;
            }

            EventMetadata<T>.Descriptor = new StructDescriptor<T>(ValidateAndCopy(routes));
        }

        /// <summary>
        /// Registers the generated category routes for a concrete class event.
        /// </summary>
        /// <typeparam name="T">The concrete, non-abstract class event type.</typeparam>
        /// <param name="routes">The unique, non-null generated routes to retain for this type.</param>
        /// <remarks>
        /// The initial route array is copied after validation. The descriptor checks null payloads
        /// and exact runtime class identity before publication; subsequent registration calls are ignored.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The initial <paramref name="routes"/> array is null.</exception>
        /// <exception cref="ArgumentException">
        /// The event type is abstract, or the initial array contains a null route or duplicate category.
        /// </exception>
        public static void RegisterClass<T>(params GeneratedRoute<T>[] routes) where T : class, IEvent
        {
            if (EventMetadata<T>.Descriptor != null)
            {
                return;
            }

            if (typeof(T).IsAbstract)
            {
                throw new ArgumentException("Abstract event types are categories, not concrete payloads.");
            }

            EventMetadata<T>.Descriptor = new ClassDescriptor<T>(ValidateAndCopy(routes));
        }

        private static GeneratedRoute<T>[] ValidateAndCopy<T>(GeneratedRoute<T>[] routes) where T : IEvent
        {
            if (routes == null)
            {
                throw new ArgumentNullException(nameof(routes));
            }

            for (int i = 0; i < routes.Length; i++)
            {
                ValidateRoute(routes, i);
            }

            return (GeneratedRoute<T>[])routes.Clone();
        }

        private static void ValidateRoute<T>(GeneratedRoute<T>[] routes, int index) where T : IEvent
        {
            if (routes[index] == null)
            {
                throw new ArgumentException("Generated routes cannot contain null.", nameof(routes));
            }

            // Registration is infrequent. A small pairwise scan avoids retaining another lookup
            // structure, while duplicate categories would otherwise duplicate subscriber delivery.
            for (int previous = 0; previous < index; previous++)
            {
                if (routes[index].CategoryType == routes[previous].CategoryType)
                {
                    throw new ArgumentException("Generated category routes must be unique.", nameof(routes));
                }
            }
        }
    }
}
