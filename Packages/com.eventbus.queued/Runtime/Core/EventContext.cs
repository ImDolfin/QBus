using System.Diagnostics;

namespace QueuedEventBus
{
    /// <summary>
    /// Describes one publication of an event, independently of its payload and subscribers.
    /// </summary>
    /// <remarks>
    /// The bus captures this immutable value during Raise or RaiseImmediate alongside the payload.
    /// Every subscriber receiving that publication observes the same context. Raising the same
    /// payload again creates a new context without modifying the payload or an earlier context.
    /// A default-initialized context does not represent a publication; its sequence is zero.
    /// </remarks>
    public readonly struct EventContext
    {
        internal EventContext(long raisedTimestamp, long sequence)
        {
            RaisedTimestamp = raisedTimestamp;
            Sequence = sequence;
        }

        /// <summary>
        /// Gets the elapsed-time clock reading captured when the bus accepts the publication.
        /// </summary>
        /// <value>The value returned by <see cref="Stopwatch.GetTimestamp"/> during Raise or RaiseImmediate.</value>
        /// <remarks>
        /// This is a raw clock reading, not UTC, DateTime ticks, or Unity's scaled game time.
        /// To measure age in seconds, subtract this value from a later Stopwatch.GetTimestamp()
        /// reading and divide by <see cref="TimestampFrequency"/> using floating-point arithmetic.
        /// Readings use the platform's Stopwatch clock and are meaningful within that clock's lifetime;
        /// do not persist them as wall-clock dates or compare them between machines.
        /// </remarks>
        public long RaisedTimestamp { get; }

        /// <summary>
        /// Gets the publication's increasing identifier within its owning bus.
        /// </summary>
        /// <value>A positive identifier starting at one for each new bus instance.</value>
        /// <remarks>
        /// All event types and both delivery modes on a bus share this sequence. Rejected publications
        /// do not consume identifiers. IDs describe publication order, not callback execution order,
        /// and are not globally unique. The default Unity bus starts a new sequence each Play session.
        /// </remarks>
        public long Sequence { get; }

        /// <summary>
        /// Gets the number of timestamp clock ticks per second.
        /// </summary>
        /// <value>The frequency of the clock used by <see cref="RaisedTimestamp"/>.</value>
        public static long TimestampFrequency
        {
            get
            {
                return Stopwatch.Frequency;
            }
        }
    }
}
