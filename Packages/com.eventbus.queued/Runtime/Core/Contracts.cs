using System;

namespace QueuedEventBus
{
    /// <summary>
    /// Identifies an event payload or a category of related event payloads.
    /// </summary>
    /// <remarks>
    /// Implement this interface on concrete event classes and structs. Category interfaces and
    /// base classes also implement it so generated routes can connect their concrete events.
    /// Immutable structs avoid payload-construction allocations and are recommended for frequent events.
    /// </remarks>
    public interface IEvent
    {
    }

    /// <summary>
    /// Handles a notification of one exact concrete event type.
    /// </summary>
    /// <typeparam name="T">The concrete event type accepted by the callback.</typeparam>
    /// <param name="message">The event payload, passed by read-only reference.</param>
    /// <remarks>
    /// The read-only reference prevents replacing or directly modifying a value-type parameter.
    /// It does not make objects referenced by the payload immutable.
    /// </remarks>
    public delegate void EventCallback<T>(in T message) where T : IEvent;

    /// <summary>
    /// Handles one exact concrete event type together with its publication metadata.
    /// </summary>
    /// <typeparam name="T">The concrete event type accepted by the callback.</typeparam>
    /// <param name="message">The event payload, passed by read-only reference.</param>
    /// <param name="context">The immutable metadata captured for this particular publication.</param>
    /// <remarks>
    /// The context belongs to the queued publication, so repeated raises of the same payload
    /// have independent metadata. Copy the context value if it is needed after the callback returns.
    /// Read-only references do not make objects referenced by the payload immutable.
    /// </remarks>
    public delegate void EventCallbackWithContext<T>(in T message, in EventContext context) where T : IEvent;

    /// <summary>
    /// Handles all events belonging to a category while preserving their concrete payload types.
    /// </summary>
    /// <typeparam name="TBase">The event interface, base class, or concrete type to observe.</typeparam>
    /// <remarks>
    /// Generated adapters invoke the generic handler without converting struct payloads into
    /// interface objects. Access category members through the constrained generic parameter to
    /// preserve this allocation-free behavior; assigning it to an interface variable can box it.
    /// </remarks>
    public interface IEventHandler<TBase> where TBase : IEvent
    {
        /// <summary>
        /// Processes one event belonging to the subscribed category.
        /// </summary>
        /// <typeparam name="T">The concrete event type implementing or inheriting <typeparamref name="TBase"/>.</typeparam>
        /// <param name="message">The event payload, passed by read-only reference.</param>
        void Handle<T>(in T message) where T : TBase;
    }

    /// <summary>
    /// Handles an event category with publication metadata while preserving each concrete payload type.
    /// </summary>
    /// <typeparam name="TBase">The event interface, base class, or concrete type to observe.</typeparam>
    /// <remarks>
    /// Generated routes preserve struct payloads without boxing them into their category interface.
    /// Access category members through the constrained generic parameter to retain this behavior.
    /// Each subscription is independent, including subscriptions of a handler through both this
    /// interface and <see cref="IEventHandler{TBase}"/>.
    /// </remarks>
    public interface IEventHandlerWithContext<TBase> where TBase : IEvent
    {
        /// <summary>
        /// Processes one event belonging to the subscribed category and its publication metadata.
        /// </summary>
        /// <typeparam name="T">The concrete event type implementing or inheriting <typeparamref name="TBase"/>.</typeparam>
        /// <param name="message">The event payload, passed by read-only reference.</param>
        /// <param name="context">The immutable metadata shared by this publication's subscribers.</param>
        void Handle<T>(in T message, in EventContext context) where T : TBase;
    }

    /// <summary>
    /// Represents one subscription whose lifetime is controlled by its owner.
    /// </summary>
    /// <remarks>
    /// Copies refer to the same subscription. Disposing any copy removes that subscription once;
    /// later disposal calls, default-initialized handles, and handles from disposed buses are harmless.
    /// Keep the value in its concrete type to avoid boxing it as <see cref="IDisposable"/>.
    /// </remarks>
    public readonly struct Subscription : IDisposable
    {
        private readonly EventBus _bus;
        private readonly long _id;

        internal Subscription(EventBus bus, long id)
        {
            _bus = bus;
            _id = id;
        }

        /// <summary>
        /// Removes the subscription if it is still registered.
        /// </summary>
        /// <remarks>
        /// Removal during dispatch affects subsequent events. A callback already captured in the
        /// current event's delivery snapshot still runs. The operation can allocate when rebuilding routes.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The owning bus is still active and the caller is not its owning thread.
        /// </exception>
        public void Dispose()
        {
            _bus?.Unsubscribe(_id);
        }
    }
}
