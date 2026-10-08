using UnityEngine;

namespace QueuedEventBus.Samples
{
    /// <summary>
    /// Demonstrates an exact-type subscription tied to this component's enabled lifetime.
    /// </summary>
    /// <remarks>
    /// Only <see cref="DamageEvent"/> notifications reach this listener. Logging is
    /// illustrative and allocates memory.
    /// </remarks>
    public sealed class DamageListener : MonoBehaviour
    {
        private Subscription _subscription;

        private void OnEnable()
        {
            _subscription = Events.Subscribe<DamageEvent>(OnDamage);
        }

        private void OnDisable()
        {
            _subscription.Dispose();
        }

        private static void OnDamage(in DamageEvent message)
        {
            // Logging and interpolated strings allocate; the bus does not require either.
            Debug.Log($"Damage: target {message.TargetId}, amount {message.Amount}");
        }
    }
}
