using QueuedEventBus.Generated;

namespace QueuedEventBus.Tests
{
    internal interface ICategory : IEvent
    {
    }

    internal interface ILeft : ICategory
    {
    }

    internal interface IRight : ICategory
    {
    }

    internal interface IDiamond : ILeft, IRight
    {
    }

    internal interface IValuedEvent : IEvent
    {
        /// <summary>
        /// Gets the value exposed through the event category.
        /// </summary>
        int Value
        {
            get;
        }
    }

    internal readonly struct ValuedEvent : IValuedEvent
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

    internal readonly struct Number : IDiamond
    {
        /// <summary>
        /// The sequence or payload value carried by this fixture.
        /// </summary>
        public readonly int Value;

        /// <summary>
        /// Creates an ordered diamond-category message.
        /// </summary>
        /// <param name="value">The sequence value carried by the message.</param>
        public Number(int value)
        {
            Value = value;
        }
    }

    internal readonly struct OtherNumber : ICategory
    {
        /// <summary>
        /// The sequence or payload value carried by this fixture.
        /// </summary>
        public readonly int Value;

        /// <summary>
        /// Creates a message for the second category channel.
        /// </summary>
        /// <param name="value">The sequence value carried by the message.</param>
        public OtherNumber(int value)
        {
            Value = value;
        }
    }

    internal readonly struct ThirdNumber : IEvent
    {
        /// <summary>
        /// The sequence or payload value carried by this fixture.
        /// </summary>
        public readonly int Value;

        /// <summary>
        /// Creates a message for a lazily activated channel.
        /// </summary>
        /// <param name="value">The value carried by the message.</param>
        public ThirdNumber(int value)
        {
            Value = value;
        }
    }

    internal struct ReferenceEvent : IEvent
    {
        /// <summary>
        /// The payload reference whose lifetime is observed by the test.
        /// </summary>
        public object Reference;
    }

    internal class BaseMessage : ICategory
    {
    }

    internal sealed class DerivedMessage : BaseMessage, ILeft
    {
    }

    internal abstract class AbstractMessage : IEvent
    {
    }

    internal struct MissingMetadata : IEvent
    {
    }

    internal static class FixtureRegistration
    {
        /// <summary>
        /// Registers the handwritten descriptors used by the standalone runtime test harness.
        /// </summary>
        public static void Register()
        {
            // The standalone harness exercises runtime behavior independently of the generator.
            // These descriptors mirror generated routes; Unity tests cover actual generator discovery.
            GeneratedEventCatalog.RegisterStruct<ValuedEvent>(
                GeneratedRoutes.Create<ValuedEvent, ValuedEvent>(),
                GeneratedRoutes.Create<ValuedEvent, IValuedEvent>(),
                GeneratedRoutes.Create<ValuedEvent, IEvent>());
            GeneratedEventCatalog.RegisterStruct<Number>(
                GeneratedRoutes.Create<Number, Number>(),
                GeneratedRoutes.Create<Number, IDiamond>(),
                GeneratedRoutes.Create<Number, ILeft>(),
                GeneratedRoutes.Create<Number, IRight>(),
                GeneratedRoutes.Create<Number, ICategory>(),
                GeneratedRoutes.Create<Number, IEvent>());
            GeneratedEventCatalog.RegisterStruct<OtherNumber>(
                GeneratedRoutes.Create<OtherNumber, OtherNumber>(),
                GeneratedRoutes.Create<OtherNumber, ICategory>(),
                GeneratedRoutes.Create<OtherNumber, IEvent>());
            GeneratedEventCatalog.RegisterStruct<ThirdNumber>(
                GeneratedRoutes.Create<ThirdNumber, ThirdNumber>(),
                GeneratedRoutes.Create<ThirdNumber, IEvent>());
            GeneratedEventCatalog.RegisterStruct<ReferenceEvent>(
                GeneratedRoutes.Create<ReferenceEvent, ReferenceEvent>(),
                GeneratedRoutes.Create<ReferenceEvent, IEvent>());
            GeneratedEventCatalog.RegisterClass<BaseMessage>(
                GeneratedRoutes.Create<BaseMessage, BaseMessage>(),
                GeneratedRoutes.Create<BaseMessage, ICategory>(),
                GeneratedRoutes.Create<BaseMessage, IEvent>());
            GeneratedEventCatalog.RegisterClass<DerivedMessage>(
                GeneratedRoutes.Create<DerivedMessage, DerivedMessage>(),
                GeneratedRoutes.Create<DerivedMessage, BaseMessage>(),
                GeneratedRoutes.Create<DerivedMessage, ILeft>(),
                GeneratedRoutes.Create<DerivedMessage, ICategory>(),
                GeneratedRoutes.Create<DerivedMessage, IEvent>());
        }
    }
}
