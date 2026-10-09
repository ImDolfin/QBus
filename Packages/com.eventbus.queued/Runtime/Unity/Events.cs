using System;
using UnityEngine;

namespace QueuedEventBus
{
    /// <summary>
    /// Provides Unity's automatically managed event bus on the main thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this facade from <c>Awake</c> onward during Play mode. The bus is reset for each Play
    /// session, including sessions with domain reload disabled. Use an explicitly owned
    /// <see cref="EventBus"/> for EditMode tools or manual delivery.
    /// </para>
    /// <para>
    /// Queued delivery runs immediately after Unity's <c>Update.ScriptRunBehaviourUpdate</c> system.
    /// Each pass captures all pending channel counts before invoking any listener. Events
    /// enqueued during delivery wait for the next pass. Queued events retain FIFO order within
    /// their concrete channel; there is no global ordering guarantee across channels.
    /// <see cref="RaiseImmediate{T}(in T)"/> delivers synchronously and can overtake queued events.
    /// </para>
    /// <para>
    /// Subscribers run in registration order. Each event captures its listeners at delivery
    /// time, so subscription changes affect subsequent events, including those in the current
    /// pass. Listener exceptions are reported and delivery continues.
    /// </para>
    /// <para>
    /// Warmed publication and delivery allocate no bus memory when queue capacity is sufficient
    /// and subscriptions are unchanged. Initialization, queue growth, subscription changes,
    /// exception reporting, and application callbacks can allocate.
    /// </para>
    /// </remarks>
    public static class Events
    {
        private static EventBus _bus;
        private static bool _metadataReady;
        private static int _dispatchDepth;
        private static bool _shutdownRequested;

        #region Public API

        /// <summary>
        /// Gets or sets the callback used to report listener exceptions.
        /// </summary>
        /// <value>
        /// The current exception reporter. Assigning <see langword="null"/> restores Unity
        /// exception logging, which is also the default for each Play session.
        /// </value>
        /// <remarks>
        /// Access this property on the Unity main thread. If the reporter throws, the bus
        /// attempts Unity logging and suppresses logging failures so queued delivery continues.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, or the caller is not its owner thread.
        /// </exception>
        public static Action<Exception> ExceptionReporter
        {
            get
            {
                return DefaultBus.ExceptionReporter;
            }
            set
            {
                DefaultBus.ExceptionReporter = value;
            }
        }

        /// <summary>
        /// Enqueues a concrete event for the next automatic delivery pass.
        /// </summary>
        /// <typeparam name="T">The exact concrete event type with registered generated metadata.</typeparam>
        /// <param name="message">The event payload to copy into its typed queue.</param>
        /// <remarks>
        /// Call on the Unity main thread. Calls to this method from a listener wait until the next pass.
        /// Class payloads are queued by reference; keep their data stable until delivery.
        /// Each accepted publication receives an immutable <see cref="EventContext"/> with
        /// its raise timestamp and a sequence number from this Play session's bus.
        /// The first publication creates the channel, and exceeding its retained capacity
        /// grows the queue. Use <see cref="Prewarm{T}(int)"/> to reserve capacity in advance.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, the caller is not its owner thread,
        /// or generated metadata is unavailable for <typeparamref name="T"/>.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="message"/> is a null class event.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// The class payload's runtime type differs from <typeparamref name="T"/>.
        /// </exception>
        /// <exception cref="OverflowException">
        /// The publication sequence is exhausted or queue capacity cannot be increased.
        /// </exception>
        public static void Raise<T>(in T message) where T : IEvent
        {
            DefaultBus.Raise(in message);
        }

        /// <summary>
        /// Delivers a concrete event synchronously before returning to its publisher.
        /// </summary>
        /// <typeparam name="T">The exact concrete event type with registered generated metadata.</typeparam>
        /// <param name="message">The payload delivered to the current matching subscribers.</param>
        /// <remarks>
        /// Call on the Unity main thread. Exact and category subscribers share one callback
        /// snapshot in registration order. Metadata uses the same raise-time clock and
        /// publication sequence as queued events. Pending queues are not drained or reordered,
        /// so immediate delivery can overtake an earlier queued event of the same type.
        /// Nested immediate events finish before the enclosing listener resumes; at most 64
        /// immediate publications may be active at once. Ordinary <see cref="Raise{T}(in T)"/>
        /// calls still enqueue for a subsequent delivery pass. Listener exceptions are reported
        /// and delivery continues. Requested Unity teardown waits for the outermost dispatch.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, the caller is not its owner thread,
        /// generated metadata is unavailable, or the immediate nesting limit is reached.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="message"/> is a null class event.</exception>
        /// <exception cref="ArgumentException">
        /// The class payload's runtime type differs from <typeparamref name="T"/>.
        /// </exception>
        /// <exception cref="OverflowException">The publication sequence is exhausted.</exception>
        public static void RaiseImmediate<T>(in T message) where T : IEvent
        {
            var bus = DefaultBus;
            bus.CheckAccess();
            _dispatchDepth++;

            try
            {
                bus.RaiseImmediate(in message);
            }
            finally
            {
                CompleteDispatch();
            }
        }

        /// <summary>
        /// Subscribes a callback to exactly one concrete event type.
        /// </summary>
        /// <typeparam name="T">The exact concrete event type to observe.</typeparam>
        /// <param name="callback">The callback invoked for each matching event.</param>
        /// <returns>A copyable token that removes this subscription when disposed.</returns>
        /// <remarks>
        /// Call and dispose the returned token on the Unity main thread. Subscribing allocates
        /// and updates cached delivery arrays. Subscribers are selected when each event is
        /// delivered; changes made during a callback affect the next event. Separate calls
        /// create separate deliveries, even for the same callback. Callback exceptions are
        /// reported through <see cref="ExceptionReporter"/> and do not stop delivery.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, the caller is not its owner thread,
        /// or generated metadata is unavailable for <typeparamref name="T"/>.
        /// </exception>
        public static Subscription Subscribe<T>(EventCallback<T> callback) where T : IEvent
        {
            return DefaultBus.Subscribe(callback);
        }

        /// <summary>
        /// Subscribes a callback to one concrete event type and its publication metadata.
        /// </summary>
        /// <typeparam name="T">The exact concrete event type to observe.</typeparam>
        /// <param name="callback">The callback receiving the payload and its immutable context.</param>
        /// <returns>A copyable token that removes this subscription when disposed.</returns>
        /// <remarks>
        /// Call and dispose the returned token on the Unity main thread. Each raise receives
        /// its own context, even when the same class instance is published repeatedly.
        /// Context-aware and payload-only listeners share registration order and the same
        /// per-event subscription snapshot. Subscribing allocates; warmed delivery does not.
        /// Callback exceptions are reported through <see cref="ExceptionReporter"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, the caller is not its owner thread,
        /// or generated routing metadata is unavailable for <typeparamref name="T"/>.
        /// </exception>
        public static Subscription Subscribe<T>(EventCallbackWithContext<T> callback) where T : IEvent
        {
            return DefaultBus.Subscribe(callback);
        }

        /// <summary>
        /// Subscribes a handler to concrete events implementing or inheriting an event category.
        /// </summary>
        /// <typeparam name="TBase">The event interface, base class, or concrete event type to observe.</typeparam>
        /// <param name="handler">The handler invoked with each matching concrete event type.</param>
        /// <returns>A copyable token that removes this subscription when disposed.</returns>
        /// <remarks>
        /// Call and dispose the returned token on the Unity main thread. A concrete category
        /// also matches its own events. Generated routes preserve the concrete payload type,
        /// including for structs, without boxing it to the category. Subscribing allocates
        /// and updates cached delivery arrays. Changes made during a callback affect the next
        /// event; separate calls create separate deliveries even for the same handler.
        /// Handler exceptions are reported through <see cref="ExceptionReporter"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, or the caller is not its owner thread.
        /// </exception>
        public static Subscription Subscribe<TBase>(IEventHandler<TBase> handler) where TBase : IEvent
        {
            return DefaultBus.Subscribe(handler);
        }

        /// <summary>
        /// Subscribes a category handler that also receives each publication's metadata.
        /// </summary>
        /// <typeparam name="TBase">The event interface, base class, or concrete event type to observe.</typeparam>
        /// <param name="handler">The handler receiving each matching concrete payload and context.</param>
        /// <returns>A copyable token that removes this subscription when disposed.</returns>
        /// <remarks>
        /// Call and dispose the returned token on the Unity main thread. Generated routes
        /// preserve the concrete payload type without boxing structs. Each matching listener
        /// receives the context captured at raise time. Changes during a callback affect the
        /// next event; separate subscriptions produce separate deliveries in registration order.
        /// Subscribing allocates. Handler exceptions are reported through <see cref="ExceptionReporter"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, or the caller is not its owner thread.
        /// </exception>
        public static Subscription Subscribe<TBase>(IEventHandlerWithContext<TBase> handler) where TBase : IEvent
        {
            return DefaultBus.Subscribe(handler);
        }

        /// <summary>
        /// Creates a concrete channel if necessary and reserves its retained queue capacity.
        /// </summary>
        /// <typeparam name="T">The concrete event type with registered generated metadata.</typeparam>
        /// <param name="capacity">The nonnegative minimum number of queued events to reserve.</param>
        /// <remarks>
        /// Call on the Unity main thread before a publication burst. Capacity is at least 16
        /// and grows in powers of two; this method never shrinks an existing queue. Channel
        /// creation also reserves scheduler storage. Prewarming itself can allocate.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">
        /// The default bus is not ready, is shutting down, the caller is not its owner thread,
        /// or generated metadata is unavailable for <typeparamref name="T"/>.
        /// </exception>
        /// <exception cref="OverflowException">
        /// The requested capacity cannot fit in the queue's power-of-two size representation.
        /// </exception>
        public static void Prewarm<T>(int capacity) where T : IEvent
        {
            DefaultBus.Prewarm<T>(capacity);
        }

        #endregion

        #region Lifecycle state

        internal static bool IsReady
        {
            get
            {
                return _metadataReady && _bus != null && !_shutdownRequested;
            }
        }

        internal static EventBus DefaultBus
        {
            get
            {
                if (!IsReady)
                {
                    throw new InvalidOperationException(
                        "QueuedEventBus.Events is not initialized. Use it from Awake onward during Play mode. " +
                        "For EditMode tools and tests, own an EventBus instance and pump it explicitly.");
                }

                return _bus;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset()
        {
            Shutdown();
            _metadataReady = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        internal static void MarkReady()
        {
            // All generated AfterAssembliesLoaded registration callbacks have finished.
            _metadataReady = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void Initialize()
        {
            if (!_metadataReady)
            {
                throw new InvalidOperationException("QueuedEventBus metadata has not completed registration.");
            }

            if (_bus != null)
            {
                return;
            }

            var bus = new EventBus(Debug.LogException);

            try
            {
                EventBusPlayerLoop.Install();
            }
            catch
            {
                bus.Dispose();
                throw;
            }

            _bus = bus;
            _shutdownRequested = false;

            // Reattaching is safe when entering Play mode without a domain reload.
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
        }

        internal static void Dispatch()
        {
            if (!IsReady)
            {
                return;
            }

            var bus = DefaultBus;
            bus.CheckAccess();

            if (_dispatchDepth != 0)
            {
                throw new InvalidOperationException("Recursive default-bus pumping is not supported.");
            }

            _dispatchDepth++;

            try
            {
                bus.Pump();
            }
            finally
            {
                CompleteDispatch();
            }
        }

        private static void CompleteDispatch()
        {
            _dispatchDepth--;

            if (_dispatchDepth == 0 && _shutdownRequested)
            {
                Shutdown();
            }
        }

        internal static void Shutdown()
        {
            _metadataReady = false;

            if (_dispatchDepth != 0)
            {
                // Editor stop or an application exit can be requested by a listener.
                // Nested immediate dispatch shares this lifetime with a containing pump.
                // Dispose only after every active dispatch releases its callback snapshot.
                _shutdownRequested = true;
                return;
            }

            Application.quitting -= Shutdown;
            EventBusPlayerLoop.Remove();

            var bus = _bus;
            _bus = null;
            _shutdownRequested = false;
            bus?.Dispose();
        }

        #endregion
    }
}
