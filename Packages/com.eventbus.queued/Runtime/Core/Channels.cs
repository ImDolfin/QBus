using System;
using System.Collections.Generic;

namespace QueuedEventBus
{
    internal abstract class ChannelBase
    {
        internal bool Scheduled;
        internal int EligibleCount;

        internal abstract int Count { get; }

        internal abstract int Drain(EventBus bus);
        internal abstract void Rebuild();
        internal abstract void Release();
    }

    internal readonly struct QueuedEvent<T> where T : IEvent
    {
        internal readonly T Message;
        internal readonly EventContext Context;

        internal QueuedEvent(in T message, in EventContext context)
        {
            Message = message;
            Context = context;
        }
    }

    internal sealed class Channel<T> : ChannelBase where T : IEvent
    {
        // Store metadata with each publication, never on a potentially reused class payload.
        private QueuedEvent<T>[] _queue = new QueuedEvent<T>[16];
        private int _head;
        private int _count;

        private readonly List<Delivery<T>> _exact = new List<Delivery<T>>();
        private BoundRoute<T>[] _routes;
        private Delivery<T>[] _deliveries = Array.Empty<Delivery<T>>();

        internal Channel(EventBus bus, EventDescriptor<T> descriptor)
        {
            _routes = new BoundRoute<T>[descriptor.Routes.Length];
            for (int i = 0; i < _routes.Length; i++)
            {
                _routes[i] = descriptor.Routes[i].Bind(bus, this);
            }

            Rebuild();
        }

        internal override int Count
        {
            get
            {
                return _count;
            }
        }

        internal void Reserve(int capacity)
        {
            if (capacity <= _queue.Length)
            {
                return;
            }

            // Doubling preserves a power-of-two capacity, allowing indices to wrap with a bit mask.
            int length = _queue.Length;
            while (length < capacity)
            {
                length = checked(length * 2);
            }

            var replacement = new QueuedEvent<T>[length];
            CopyQueuedMessagesTo(replacement);

            _queue = replacement;
            _head = 0;
        }

        private void CopyQueuedMessagesTo(QueuedEvent<T>[] replacement)
        {
            // The logical queue may straddle the buffer boundary. Copy both segments into FIFO order.
            int firstSegmentLength = Math.Min(_count, _queue.Length - _head);
            Array.Copy(_queue, _head, replacement, 0, firstSegmentLength);
            Array.Copy(_queue, 0, replacement, firstSegmentLength, _count - firstSegmentLength);
        }

        internal void Enqueue(in T message, in EventContext context)
        {
            if (_count == _queue.Length)
            {
                Reserve(checked(_count + 1));
            }

            _queue[(_head + _count) & (_queue.Length - 1)] = new QueuedEvent<T>(in message, in context);
            _count++;
        }

        internal void Add(Delivery<T> subscription)
        {
            _exact.Add(subscription);
            Rebuild();
        }

        internal void Remove(Delivery<T> subscription)
        {
            _exact.Remove(subscription);
            Rebuild();
        }

        internal override void Rebuild()
        {
            var deliveries = new List<Delivery<T>>(_exact);
            for (int i = 0; i < _routes.Length; i++)
            {
                _routes[i].Append(deliveries);
            }

            deliveries.Sort(DeliveryComparer<T>.Instance);
            _deliveries = deliveries.Count == 0 ? Array.Empty<Delivery<T>>() : deliveries.ToArray();
        }

        internal override int Drain(EventBus bus)
        {
            int count = EligibleCount;
            for (int i = 0; i < count; i++)
            {
                // Keep a typed local: a callback may grow the queue and replace its backing array.
                Dequeue(out T message, out EventContext context);
                Deliver(bus, in message, in context);
            }

            return count;
        }

        private void Dequeue(out T message, out EventContext context)
        {
            // Write directly into the caller's local so extracting this helper does not add
            // a return-value copy for large struct payloads.
            message = _queue[_head].Message;
            context = _queue[_head].Context;
            _queue[_head] = default;
            _head = (_head + 1) & (_queue.Length - 1);
            _count--;
        }

        // Both publication modes use this typed path. Immediate delivery never changes queue
        // counts or scheduler state, even when invoked while this channel is being drained.
        internal void Deliver(EventBus bus, in T message, in EventContext context)
        {
            // Subscription changes replace the array. Capture once per event so its callbacks finish
            // consistently, while the next event observes any changes made during this delivery.
            var deliveries = _deliveries;
            for (int i = 0; i < deliveries.Length; i++)
            {
                try
                {
                    deliveries[i].Invoke(in message, in context);
                }
                catch (Exception error)
                {
                    bus.Report(error);
                }
            }
        }

        internal override void Release()
        {
            _queue = Array.Empty<QueuedEvent<T>>();
            _count = 0;
            _head = 0;

            _deliveries = Array.Empty<Delivery<T>>();
            _exact.Clear();
            _routes = Array.Empty<BoundRoute<T>>();
        }
    }

    internal interface ISubscriptionRecord
    {
        /// <summary>
        /// Removes this record from its owning channel or category and rebuilds affected deliveries.
        /// </summary>
        void Remove();
    }

    internal abstract class Delivery<T> where T : IEvent
    {
        internal readonly long Id;

        protected Delivery(long id)
        {
            Id = id;
        }

        internal abstract void Invoke(in T message, in EventContext context);
    }

    internal sealed class DeliveryComparer<T> : IComparer<Delivery<T>> where T : IEvent
    {
        internal static readonly DeliveryComparer<T> Instance = new DeliveryComparer<T>();

        /// <summary>
        /// Compares deliveries by their subscription identifiers to preserve registration order.
        /// </summary>
        /// <param name="x">The first non-null delivery.</param>
        /// <param name="y">The second non-null delivery.</param>
        /// <returns>A negative value, zero, or a positive value when the first subscription is earlier, equal, or later.</returns>
        public int Compare(Delivery<T> x, Delivery<T> y)
        {
            return x.Id.CompareTo(y.Id);
        }
    }

    internal sealed class ExactSubscription<T> : Delivery<T>, ISubscriptionRecord where T : IEvent
    {
        private readonly Channel<T> _channel;
        private readonly EventCallback<T> _callback;

        internal ExactSubscription(long id, Channel<T> channel, EventCallback<T> callback)
            : base(id)
        {
            _channel = channel;
            _callback = callback;
        }

        internal override void Invoke(in T message, in EventContext context)
        {
            _callback(in message);
        }

        /// <summary>
        /// Removes this exact subscription and rebuilds its channel's delivery snapshot.
        /// </summary>
        public void Remove()
        {
            _channel.Remove(this);
        }
    }

    internal abstract class CategoryBucketBase
    {
        internal readonly List<ChannelBase> Dependents = new List<ChannelBase>();

        protected void Changed()
        {
            for (int i = 0; i < Dependents.Count; i++)
            {
                Dependents[i].Rebuild();
            }
        }

        internal abstract void Release();
    }

    internal sealed class CategoryBucket<T> : CategoryBucketBase where T : IEvent
    {
        internal readonly List<CategorySubscription<T>> Subscriptions = new List<CategorySubscription<T>>();

        internal void Add(CategorySubscription<T> record)
        {
            Subscriptions.Add(record);
            Changed();
        }

        internal void Remove(CategorySubscription<T> record)
        {
            Subscriptions.Remove(record);
            Changed();
        }

        internal override void Release()
        {
            Subscriptions.Clear();
            Dependents.Clear();
        }
    }

    internal sealed class CategorySubscription<T> : ISubscriptionRecord where T : IEvent
    {
        internal readonly long Id;
        internal readonly IEventHandler<T> Handler;
        internal readonly IEventHandlerWithContext<T> ContextHandler;

        private readonly CategoryBucket<T> _bucket;

        internal CategorySubscription(long id, CategoryBucket<T> bucket, IEventHandler<T> handler)
            : this(id, bucket)
        {
            Handler = handler;
        }

        internal CategorySubscription(long id, CategoryBucket<T> bucket, IEventHandlerWithContext<T> handler)
            : this(id, bucket)
        {
            ContextHandler = handler;
        }

        private CategorySubscription(long id, CategoryBucket<T> bucket)
        {
            Id = id;
            _bucket = bucket;
        }

        /// <summary>
        /// Removes this category subscription and rebuilds all dependent channel snapshots.
        /// </summary>
        public void Remove()
        {
            _bucket.Remove(this);
        }
    }

    internal abstract class BoundRoute<T> where T : IEvent
    {
        internal abstract void Append(List<Delivery<T>> deliveries);
    }

    internal sealed class CategoryBoundRoute<TEvent, TCategory> : BoundRoute<TEvent>
        where TEvent : TCategory
        where TCategory : IEvent
    {
        private readonly CategoryBucket<TCategory> _bucket;

        internal CategoryBoundRoute(CategoryBucket<TCategory> bucket)
        {
            _bucket = bucket;
        }

        internal override void Append(List<Delivery<TEvent>> deliveries)
        {
            for (int i = 0; i < _bucket.Subscriptions.Count; i++)
            {
                var record = _bucket.Subscriptions[i];

                // Select an adapter while rebuilding, keeping the dispatch path branch-free.
                // Both adapters use the generated closed event/category pair for AOT compilation.
                if (record.ContextHandler != null)
                {
                    deliveries.Add(new ContextCategoryDelivery<TEvent, TCategory>(record.Id, record.ContextHandler));
                }
                else
                {
                    deliveries.Add(new CategoryDelivery<TEvent, TCategory>(record.Id, record.Handler));
                }
            }
        }
    }

    internal sealed class CategoryDelivery<TEvent, TCategory> : Delivery<TEvent>
        where TEvent : TCategory
        where TCategory : IEvent
    {
        private readonly IEventHandler<TCategory> _handler;

        internal CategoryDelivery(long id, IEventHandler<TCategory> handler)
            : base(id)
        {
            _handler = handler;
        }

        internal override void Invoke(in TEvent message, in EventContext context)
        {
            // The generic constraint proves category membership without converting the event to
            // its category interface, so struct payloads remain unboxed during category delivery.
            _handler.Handle<TEvent>(in message);
        }
    }
}
