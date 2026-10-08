using System.Diagnostics;
using UnityEngine;

namespace QueuedEventBus.Samples
{
    /// <summary>
    /// Demonstrates exact and category subscriptions that read automatic publication metadata.
    /// </summary>
    /// <remarks>
    /// Add this component beside <see cref="CombatPublisher"/> to inspect publication sequences
    /// and time spent waiting for delivery. Logging is illustrative and allocates memory.
    /// </remarks>
    public sealed class MetadataListener : MonoBehaviour, IEventHandlerWithContext<ICombatEvent>
    {
        private Subscription _damageSubscription;
        private Subscription _combatSubscription;

        private void OnEnable()
        {
            _damageSubscription = Events.Subscribe<DamageEvent>(OnDamage);
            _combatSubscription = Events.Subscribe<ICombatEvent>(this);
        }

        private void OnDisable()
        {
            _damageSubscription.Dispose();
            _combatSubscription.Dispose();
        }

        private static void OnDamage(in DamageEvent message, in EventContext context)
        {
            UnityEngine.Debug.Log($"Damage publication {context.Sequence}: amount {message.Amount}");
        }

        /// <summary>
        /// Logs a combat publication's identity and elapsed time since it was raised.
        /// </summary>
        /// <typeparam name="T">The concrete event type routed to this category handler.</typeparam>
        /// <param name="message">The read-only combat notification.</param>
        /// <param name="context">The immutable metadata captured when this publication was raised.</param>
        public void Handle<T>(in T message, in EventContext context) where T : ICombatEvent
        {
            // Stopwatch timestamps use a clock-specific frequency and arbitrary origin.
            // Subtract first, then convert the difference; this is independent of timeScale.
            var elapsedTicks = Stopwatch.GetTimestamp() - context.RaisedTimestamp;
            var elapsedMilliseconds = elapsedTicks * 1000.0 / EventContext.TimestampFrequency;

            UnityEngine.Debug.Log(
                $"Combat publication {context.Sequence}: target {message.TargetId}, queued {elapsedMilliseconds:F3} ms");
        }
    }
}
