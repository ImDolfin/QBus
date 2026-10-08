using QueuedEventBus.Tests.Fixtures;

namespace QueuedEventBus.Tests.OtherEvents
{
    /// <summary>
    /// Declares an activity event in a different assembly from its category.
    /// </summary>
    public readonly struct SecondaryEvent : IActivity
    {
        /// <summary>
        /// The sequence or payload value carried by this fixture.
        /// </summary>
        public readonly int Value;

        /// <summary>
        /// Creates an event declared in the secondary assembly.
        /// </summary>
        /// <param name="value">The sequence value carried by the event.</param>
        public SecondaryEvent(int value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// Verifies inherited category routing across event assembly boundaries.
    /// </summary>
    public sealed class CrossAssemblyDerived : ActivityBase
    {
    }
}
