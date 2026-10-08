namespace QueuedEventBus.Samples
{
    /// <summary>
    /// Groups combat notifications that identify a target.
    /// </summary>
    public interface ICombatEvent : IEvent
    {
        /// <summary>
        /// Gets the application-defined identifier of the affected target.
        /// </summary>
        /// <value>The identifier shared by all combat events concerning the same target.</value>
        int TargetId { get; }
    }

    /// <summary>
    /// Describes a damage notification using an immutable value payload.
    /// </summary>
    public readonly struct DamageEvent : ICombatEvent
    {
        /// <summary>
        /// Creates a damage notification for a target.
        /// </summary>
        /// <param name="targetId">The application-defined identifier of the affected target.</param>
        /// <param name="amount">The reported damage amount.</param>
        public DamageEvent(int targetId, int amount)
        {
            TargetId = targetId;
            Amount = amount;
        }

        /// <summary>
        /// Gets the application-defined identifier of the damaged target.
        /// </summary>
        /// <value>The target identifier supplied by the publisher.</value>
        public int TargetId { get; }

        /// <summary>
        /// Gets the reported damage amount.
        /// </summary>
        /// <value>The amount supplied by the publisher without additional validation.</value>
        public int Amount { get; }
    }

    /// <summary>
    /// Describes a healing notification using an immutable value payload.
    /// </summary>
    public readonly struct HealEvent : ICombatEvent
    {
        /// <summary>
        /// Creates a healing notification for a target.
        /// </summary>
        /// <param name="targetId">The application-defined identifier of the affected target.</param>
        /// <param name="amount">The reported healing amount.</param>
        public HealEvent(int targetId, int amount)
        {
            TargetId = targetId;
            Amount = amount;
        }

        /// <summary>
        /// Gets the application-defined identifier of the healed target.
        /// </summary>
        /// <value>The target identifier supplied by the publisher.</value>
        public int TargetId { get; }

        /// <summary>
        /// Gets the reported healing amount.
        /// </summary>
        /// <value>The amount supplied by the publisher without additional validation.</value>
        public int Amount { get; }
    }
}
