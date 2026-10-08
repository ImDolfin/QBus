namespace QueuedEventBus
{
    internal sealed class ContextExactSubscription<T> : Delivery<T>, ISubscriptionRecord where T : IEvent
    {
        private readonly Channel<T> _channel;
        private readonly EventCallbackWithContext<T> _callback;

        internal ContextExactSubscription(long id, Channel<T> channel, EventCallbackWithContext<T> callback)
            : base(id)
        {
            _channel = channel;
            _callback = callback;
        }

        internal override void Invoke(in T message, in EventContext context)
        {
            _callback(in message, in context);
        }

        /// <summary>
        /// Removes this exact subscription and rebuilds its channel's delivery snapshot.
        /// </summary>
        public void Remove()
        {
            _channel.Remove(this);
        }
    }

    internal sealed class ContextCategoryDelivery<TEvent, TCategory> : Delivery<TEvent>
        where TEvent : TCategory
        where TCategory : IEvent
    {
        private readonly IEventHandlerWithContext<TCategory> _handler;

        internal ContextCategoryDelivery(long id, IEventHandlerWithContext<TCategory> handler)
            : base(id)
        {
            _handler = handler;
        }

        internal override void Invoke(in TEvent message, in EventContext context)
        {
            // Keep the generic constraint through the call so reading category members does not
            // box a struct payload. The context is another value passed by read-only reference.
            _handler.Handle<TEvent>(in message, in context);
        }
    }
}
