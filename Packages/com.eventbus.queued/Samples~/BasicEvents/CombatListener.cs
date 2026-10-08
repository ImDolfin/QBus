using UnityEngine;

namespace QueuedEventBus.Samples
{
    /// <summary>
    /// Demonstrates subscribing to a shared event category while this component is enabled.
    /// </summary>
    /// <remarks>
    /// Both damage and healing notifications reach this listener through their shared
    /// <see cref="ICombatEvent"/> category. Logging is illustrative and allocates memory.
    /// </remarks>
    public sealed class CombatListener : MonoBehaviour, IEventHandler<ICombatEvent>
    {
        private Subscription _subscription;

        private void OnEnable()
        {
            _subscription = Events.Subscribe<ICombatEvent>(this);
        }

        private void OnDisable()
        {
            _subscription.Dispose();
        }

        /// <summary>
        /// Logs a combat notification while preserving its concrete payload type.
        /// </summary>
        /// <typeparam name="T">The concrete event type routed to this category handler.</typeparam>
        /// <param name="message">The read-only combat notification to log.</param>
        public void Handle<T>(in T message) where T : ICombatEvent
        {
            // T remains DamageEvent or HealEvent. Reading TargetId is a constrained
            // generic call: it does not box the event into ICombatEvent.
            Debug.Log($"Combat event {typeof(T).Name}: target {message.TargetId}");
        }
    }
}
