using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace QueuedEventBus
{
    /// <summary>
    /// Queues strongly typed events for explicit delivery on the thread that constructs the bus.
    /// </summary>
    /// <remarks>
    /// Each concrete type has a FIFO queue. A pump captures every pending queue count before
    /// invoking callbacks; callback publications wait for the next pump. Ordering is guaranteed
    /// within a channel, not globally. Warmed publication and delivery allocate no bus memory.
    /// Initialization, growth, subscription changes, and exception reporting can allocate.
    /// </remarks>
    public sealed class EventBus : IDisposable
    {
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly List<ChannelBase> _createdChannels = new List<ChannelBase>(16);

        // Registration lookup is used only on subscription changes, never during publication/delivery.
        private readonly Dictionary<long, ISubscriptionRecord> _subscriptions = new Dictionary<long, ISubscriptionRecord>();
        private ChannelBase[] _channels = new ChannelBase[16];
        private CategoryBucketBase[] _categories = new CategoryBucketBase[16];

        // Swapping retained lists separates eligible work from publications made by callbacks.
        private List<ChannelBase> _incoming = new List<ChannelBase>(16);
        private List<ChannelBase> _work = new List<ChannelBase>(16);
        private long _nextSubscription;
        private long _nextSequence;
        private bool _pumping;
        private bool _disposed;
        private Action<Exception> _exceptionReporter;

        #region Public API

        /// <summary>
        /// Creates an independent bus owned by the calling thread.
        /// </summary>
        /// <param name="exceptionReporter">
        /// Reports individual listener failures. Null selects Unity exception logging in Unity,
        /// or diagnostic trace logging in the standalone runtime build.
        /// </param>
        /// <remarks>
        /// The bus does not install a PlayerLoop callback. Call <see cref="Pump"/> explicitly
        /// and <see cref="Dispose"/> when the owner is finished.
        /// </remarks>
        public EventBus(Action<Exception> exceptionReporter = null)
        {
            _exceptionReporter = exceptionReporter ?? DefaultReport;
        }

        /// <summary>
        /// Gets or sets the callback used to report listener failures.
        /// </summary>
        /// <value>The active reporter. Assign null to restore the default logger.</value>
        /// <remarks>
        /// Reporter exceptions fall back to the default logger without recursive reporting.
        /// Failures in either reporting mechanism do not stop delivery.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">The caller is not the owning thread.</exception>
        public Action<Exception> ExceptionReporter
        {
            get
            {
                CheckAccess();
                return _exceptionReporter;
            }
            set
            {
                CheckAccess();
                _exceptionReporter = value ?? DefaultReport;
            }
        }

        /// <summary>
        /// Enqueues a concrete event for delivery by a subsequent pump.
        /// </summary>
        /// <typeparam name="T">The exact concrete event type with generated routing metadata.</typeparam>
        /// <param name="message">The payload to copy into its typed queue.</param>
        /// <remarks>
        /// Structs are copied; class payloads retain the original object. Keep referenced data stable
        /// until delivery. Events raised during dispatch always wait for the next pump.
        /// Each accepted publication receives an independent timestamp and per-bus sequence in
        /// its <see cref="EventContext"/>, even when the same payload is raised more than once.
        /// </remarks>
        /// <exception cref="ArgumentNullException">A class payload is null.</exception>
        /// <exception cref="ArgumentException">The class payload's runtime type differs from <typeparamref name="T"/>.</exception>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The caller is not the owning thread, or generated metadata for <typeparamref name="T"/> is unavailable.
        /// </exception>
        /// <exception cref="OverflowException">
        /// The bus has exhausted its publication sequence or the queue cannot grow further.
        /// </exception>
        public void Raise<T>(in T message) where T : IEvent
        {
            CheckAccess();

            var descriptor = EventMetadata<T>.Require();
            descriptor.Validate(in message);

            long sequence = checked(_nextSequence + 1);
            var channel = GetOrCreateChannel(descriptor);
            var context = new EventContext(Stopwatch.GetTimestamp(), sequence);

            channel.Enqueue(in message, in context);
            Schedule(channel);
            _nextSequence = sequence;
        }

        /// <summary>
        /// Registers a callback for exactly one concrete event type.
        /// </summary>
        /// <typeparam name="T">The exact concrete event type to observe.</typeparam>
        /// <param name="callback">The callback invoked with the payload by read-only reference.</param>
        /// <returns>A handle that removes this particular registration when disposed.</returns>
        /// <remarks>
        /// Each call creates a separate subscription. Subscribers are selected at delivery time
        /// and run in registration order across exact and category handlers. Changes during a
        /// callback affect the next event, not the current snapshot. Subscription changes can allocate.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The caller is not the owning thread, or generated metadata for <typeparamref name="T"/> is unavailable.
        /// </exception>
        public Subscription Subscribe<T>(EventCallback<T> callback) where T : IEvent
        {
            CheckAccess();

            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var channel = GetOrCreateChannel(EventMetadata<T>.Require());
            var record = new ExactSubscription<T>(++_nextSubscription, channel, callback);
            channel.Add(record);
            _subscriptions.Add(record.Id, record);

            return new Subscription(this, record.Id);
        }

        /// <summary>
        /// Registers an exact-type callback that also receives per-publication metadata.
        /// </summary>
        /// <typeparam name="T">The exact concrete event type to observe.</typeparam>
        /// <param name="callback">The callback receiving the payload and context by read-only reference.</param>
        /// <returns>A handle that removes this particular registration when disposed.</returns>
        /// <remarks>
        /// Metadata is captured at publication time, while callbacks are selected at delivery time.
        /// All callbacks for one publication receive the same context. Exact and category callbacks,
        /// with or without context parameters, run together in subscription order. Subscription
        /// changes affect the next event and may allocate; warmed delivery does not allocate.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The caller is not the owning thread, or generated metadata for <typeparamref name="T"/> is unavailable.
        /// </exception>
        public Subscription Subscribe<T>(EventCallbackWithContext<T> callback) where T : IEvent
        {
            CheckAccess();

            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var channel = GetOrCreateChannel(EventMetadata<T>.Require());
            var record = new ContextExactSubscription<T>(++_nextSubscription, channel, callback);
            channel.Add(record);
            _subscriptions.Add(record.Id, record);

            return new Subscription(this, record.Id);
        }

        /// <summary>
        /// Registers a generic handler for a category and its generated concrete events.
        /// </summary>
        /// <typeparam name="T">The event interface, base class, or concrete type to observe.</typeparam>
        /// <param name="handler">The handler that preserves each delivered event's concrete type.</param>
        /// <returns>A handle that removes this particular registration when disposed.</returns>
        /// <remarks>
        /// Matching types include <typeparamref name="T"/> itself when concrete. Diamond inheritance
        /// does not duplicate delivery, but separate subscription calls remain independent.
        /// Registration order and snapshot behavior match exact subscriptions.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">The caller is not the owning thread.</exception>
        public Subscription Subscribe<T>(IEventHandler<T> handler) where T : IEvent
        {
            CheckAccess();

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var bucket = GetOrCreateCategory<T>();
            var record = new CategorySubscription<T>(++_nextSubscription, bucket, handler);
            bucket.Add(record);
            _subscriptions.Add(record.Id, record);

            return new Subscription(this, record.Id);
        }

        /// <summary>
        /// Registers a category handler that preserves concrete event types and receives publication metadata.
        /// </summary>
        /// <typeparam name="T">The event interface, base class, or concrete type to observe.</typeparam>
        /// <param name="handler">The generic handler receiving each matching payload and its context.</param>
        /// <returns>A handle that removes this particular registration when disposed.</returns>
        /// <remarks>
        /// Matching includes <typeparamref name="T"/> itself when concrete. Generated adapters avoid
        /// boxing struct payloads. This handler shares ordering and snapshot rules with all other
        /// subscriptions. Separate registrations remain separate deliveries, even for the same handler.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">The caller is not the owning thread.</exception>
        public Subscription Subscribe<T>(IEventHandlerWithContext<T> handler) where T : IEvent
        {
            CheckAccess();

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var bucket = GetOrCreateCategory<T>();
            var record = new CategorySubscription<T>(++_nextSubscription, bucket, handler);
            bucket.Add(record);
            _subscriptions.Add(record.Id, record);

            return new Subscription(this, record.Id);
        }

        /// <summary>
        /// Creates a concrete channel and reserves storage for a pending event burst.
        /// </summary>
        /// <typeparam name="T">The concrete event type to prepare.</typeparam>
        /// <param name="capacity">The minimum pending event capacity; must be nonnegative.</param>
        /// <remarks>
        /// Channels start at 16 entries and grow geometrically. Prewarming retains queued events,
        /// never shrinks capacity, and also prepares the active-channel scheduling storage.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The caller is not the owning thread, or generated metadata for <typeparamref name="T"/> is unavailable.
        /// </exception>
        public void Prewarm<T>(int capacity) where T : IEvent
        {
            CheckAccess();

            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            GetOrCreateChannel(EventMetadata<T>.Require()).Reserve(capacity);
        }

        /// <summary>
        /// Delivers every event pending when this call starts.
        /// </summary>
        /// <returns>The number of events dequeued, including events without subscribers.</returns>
        /// <remarks>
        /// All active counts are captured before callbacks run. Channels drain in activation order,
        /// without a time or event budget. Listener exceptions are reported and delivery continues.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The caller is not the owning thread, or this bus is already being pumped.
        /// </exception>
        public int Pump()
        {
            CheckAccess();

            if (_pumping)
            {
                throw new InvalidOperationException("Recursive pumping is not supported.");
            }

            BeginPump();

            try
            {
                CapturePendingCounts();
                return DrainCapturedChannels();
            }
            finally
            {
                CompletePump();
            }
        }

        /// <summary>
        /// Discards pending events and releases subscriptions and payload buffers.
        /// </summary>
        /// <remarks>
        /// Repeated calls and existing subscription handles become harmless. Other public
        /// operations reject a disposed bus. Disposal during dispatch is not permitted.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The active bus is accessed from another thread, or a pump is in progress.
        /// </exception>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CheckAccess();

            if (_pumping)
            {
                throw new InvalidOperationException("Cannot dispose a bus during dispatch.");
            }

            _disposed = true;
            _subscriptions.Clear();
            ReleaseChannelsAndCategories();
            ClearOwnedState();
        }

        #endregion

        #region Channel and subscription storage

        internal void Unsubscribe(long id)
        {
            if (_disposed)
            {
                return;
            }

            CheckAccess();

            if (!_subscriptions.TryGetValue(id, out var record))
            {
                return;
            }

            _subscriptions.Remove(id);
            record.Remove();
        }

        internal CategoryBucket<T> GetOrCreateCategory<T>() where T : IEvent
        {
            int id = EventTypeId<T>.Value;
            EnsureIndexedCapacity(ref _categories, id);

            var bucket = (CategoryBucket<T>)_categories[id];

            if (bucket != null)
            {
                return bucket;
            }

            bucket = new CategoryBucket<T>();
            _categories[id] = bucket;

            return bucket;
        }

        private Channel<T> GetOrCreateChannel<T>(EventDescriptor<T> descriptor) where T : IEvent
        {
            int id = EventTypeId<T>.Value;
            EnsureIndexedCapacity(ref _channels, id);

            var channel = (Channel<T>)_channels[id];

            if (channel != null)
            {
                return channel;
            }

            channel = new Channel<T>(this, descriptor);
            _channels[id] = channel;
            _createdChannels.Add(channel);
            ReserveSchedulingCapacity();

            return channel;
        }

        private void ReserveSchedulingCapacity()
        {
            // Each list contains a channel at most once. Reserve both here so a warmed queue
            // does not cause a scheduler allocation when it first becomes active.
            int reserve = _createdChannels.Count;

            if (_incoming.Capacity < reserve)
            {
                _incoming.Capacity = Math.Max(reserve, _incoming.Capacity * 2);
            }

            if (_work.Capacity < reserve)
            {
                _work.Capacity = Math.Max(reserve, _work.Capacity * 2);
            }
        }

        private void ReleaseChannelsAndCategories()
        {
            for (int i = 0; i < _createdChannels.Count; i++)
            {
                _createdChannels[i].Release();
            }

            for (int i = 0; i < _categories.Length; i++)
            {
                _categories[i]?.Release();
            }
        }

        private void ClearOwnedState()
        {
            _createdChannels.Clear();
            _incoming.Clear();
            _work.Clear();
            _channels = Array.Empty<ChannelBase>();
            _categories = Array.Empty<CategoryBucketBase>();
            _exceptionReporter = null;
        }

        private static void EnsureIndexedCapacity<T>(ref T[] array, int index)
        {
            if (index < array.Length)
            {
                return;
            }

            int length = array.Length;

            while (length <= index)
            {
                length = checked(length * 2);
            }

            Array.Resize(ref array, length);
        }

        #endregion

        #region Pump scheduling

        private void BeginPump()
        {
            _pumping = true;

            var previous = _work;
            _work = _incoming;
            _incoming = previous;
        }

        private void CapturePendingCounts()
        {
            // Capture ALL counts before any callback. Capturing a later channel only when it
            // is visited would incorrectly admit events raised by an earlier channel.
            for (int i = 0; i < _work.Count; i++)
            {
                var channel = _work[i];
                channel.EligibleCount = channel.Count;
                channel.Scheduled = false;
            }
        }

        private int DrainCapturedChannels()
        {
            int delivered = 0;

            for (int i = 0; i < _work.Count; i++)
            {
                delivered += _work[i].Drain(this);
            }

            return delivered;
        }

        private void CompletePump()
        {
            // Keep untouched queues reachable after unexpected internal failures. Schedule
            // also deduplicates channels already activated by a handler during this pass.
            for (int i = 0; i < _work.Count; i++)
            {
                var channel = _work[i];

                if (channel.Count != 0)
                {
                    Schedule(channel);
                }

                channel.EligibleCount = 0;
            }

            _work.Clear();
            _pumping = false;
        }

        private void Schedule(ChannelBase channel)
        {
            if (channel.Scheduled)
            {
                return;
            }

            _incoming.Add(channel);
            channel.Scheduled = true;
        }

        #endregion

        #region Error handling and access checks

        internal void Report(Exception error)
        {
            try
            {
                _exceptionReporter(error);
            }
            catch (Exception reportingError)
            {
                ReportLoggerFailure(error, reportingError);
            }
        }

        private static void ReportLoggerFailure(Exception error, Exception reportingError)
        {
            // Never recursively invoke the configured reporter when reporting itself fails.
            try
            {
                DefaultReport(error);
                DefaultReport(reportingError);
            }
            catch
            {
                // A failed platform logger must not strand pending events.
            }
        }

        private static void DefaultReport(Exception error)
        {
#if UNITY_5_3_OR_NEWER
            UnityEngine.Debug.LogException(error);
#else
            System.Diagnostics.Trace.TraceError(error.ToString());
#endif
        }

        private void CheckAccess()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EventBus));
            }

            if (Thread.CurrentThread.ManagedThreadId != _ownerThread)
            {
                throw new InvalidOperationException("EventBus must be accessed from the thread that created it.");
            }
        }

        #endregion
    }
}
