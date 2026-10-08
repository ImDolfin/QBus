using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("QueuedEventBus.Tests.EditMode")]
[assembly: InternalsVisibleTo("QueuedEventBus.Tests.PlayMode")]

namespace QueuedEventBus.Tests.Fixtures
{
    /// <summary>
    /// Marks activity events shared by the cross-assembly routing tests.
    /// </summary>
    public interface IActivity : IEvent
    {
    }

    /// <summary>
    /// Marks the local branch of the diamond activity hierarchy.
    /// </summary>
    public interface ILocalActivity : IActivity
    {
    }

    /// <summary>
    /// Marks the remote branch of the diamond activity hierarchy.
    /// </summary>
    public interface IRemoteActivity : IActivity
    {
    }

    /// <summary>
    /// Combines both activity branches to verify that shared categories are delivered once.
    /// </summary>
    public interface IDiamondActivity : ILocalActivity, IRemoteActivity
    {
    }

    /// <summary>
    /// Exposes a payload value through a category constraint to test delivery without boxing.
    /// </summary>
    public interface IValuedEvent : IEvent
    {
        /// <summary>
        /// Gets the value exposed through the event category.
        /// </summary>
        int Value
        {
            get;
        }
    }

    /// <summary>
    /// Carries a value used to verify constrained category property access.
    /// </summary>
    public readonly struct ValuedEvent : IValuedEvent
    {
        /// <summary>
        /// Gets the value exposed through the event category.
        /// </summary>
        public int Value
        {
            get;
        }

        /// <summary>
        /// Creates a valued event.
        /// </summary>
        /// <param name="value">The category property value.</param>
        public ValuedEvent(int value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// Carries an ordered value through the diamond activity hierarchy.
    /// </summary>
    public readonly struct ActivityEvent : IDiamondActivity
    {
        /// <summary>
        /// The sequence or payload value carried by this fixture.
        /// </summary>
        public readonly int Value;

        /// <summary>
        /// Creates an activity event.
        /// </summary>
        /// <param name="value">The sequence value carried by the event.</param>
        public ActivityEvent(int value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// Provides a concrete base event for exact and inherited category routing tests.
    /// </summary>
    public class ActivityBase : IActivity
    {
    }

    /// <summary>
    /// Combines class inheritance with the local activity category.
    /// </summary>
    public sealed class DerivedActivity : ActivityBase, ILocalActivity
    {
    }

    /// <summary>
    /// Contains an accessible nested event for generated metadata tests.
    /// </summary>
    public static class NestedFixture
    {
        /// <summary>
        /// Verifies generated registration for a public nested event.
        /// </summary>
        public readonly struct Event : IActivity
        {
        }
    }

    internal readonly struct InternalEvent : IActivity
    {
    }

    /// <summary>
    /// Carries a reference used to verify that queue storage releases consumed payloads.
    /// </summary>
    public struct ReferenceEvent : IEvent
    {
        /// <summary>
        /// The payload stored in this event.
        /// </summary>
        public object Payload;
    }

    /// <summary>
    /// Provides a 16-byte event payload for dispatch throughput measurements.
    /// </summary>
    public readonly struct Payload16 : IActivity
    {
        /// <summary>
        /// The first fixed-size payload section.
        /// </summary>
        public readonly long A;

        /// <summary>
        /// The second fixed-size payload section.
        /// </summary>
        public readonly long B;

        /// <summary>
        /// Initializes both 64-bit payload words.
        /// </summary>
        /// <param name="value">The value copied into each payload word.</param>
        public Payload16(long value)
        {
            A = B = value;
        }
    }

    /// <summary>
    /// Provides a 64-byte event payload for dispatch throughput measurements.
    /// </summary>
    public readonly struct Payload64 : IActivity
    {
        /// <summary>
        /// The first fixed-size payload section.
        /// </summary>
        public readonly Payload16 A;

        /// <summary>
        /// The second fixed-size payload section.
        /// </summary>
        public readonly Payload16 B;

        /// <summary>
        /// The third fixed-size payload section.
        /// </summary>
        public readonly Payload16 C;

        /// <summary>
        /// The fourth fixed-size payload section.
        /// </summary>
        public readonly Payload16 D;

        /// <summary>
        /// Initializes all four 16-byte payload sections.
        /// </summary>
        /// <param name="value">The value copied into every payload word.</param>
        public Payload64(long value)
        {
            A = B = C = D = new Payload16(value);
        }
    }

    /// <summary>
    /// Provides a 256-byte event payload for dispatch throughput measurements.
    /// </summary>
    public readonly struct Payload256 : IActivity
    {
        /// <summary>
        /// The first fixed-size payload section.
        /// </summary>
        public readonly Payload64 A;

        /// <summary>
        /// The second fixed-size payload section.
        /// </summary>
        public readonly Payload64 B;

        /// <summary>
        /// The third fixed-size payload section.
        /// </summary>
        public readonly Payload64 C;

        /// <summary>
        /// The fourth fixed-size payload section.
        /// </summary>
        public readonly Payload64 D;

        /// <summary>
        /// Initializes all four 64-byte payload sections.
        /// </summary>
        /// <param name="value">The value copied into every payload word.</param>
        public Payload256(long value)
        {
            A = B = C = D = new Payload64(value);
        }
    }
}
