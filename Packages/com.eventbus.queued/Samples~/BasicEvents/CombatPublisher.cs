using UnityEngine;

namespace QueuedEventBus.Samples
{
    /// <summary>
    /// Demonstrates reserving queue capacity and publishing queued and immediate events at startup.
    /// </summary>
    public sealed class CombatPublisher : MonoBehaviour
    {
        [SerializeField]
        private int _targetId = 1;

        private void Start()
        {
            // Optional capacity reservation, useful before a burst of publications.
            Events.Prewarm<DamageEvent>(128);
            Events.Prewarm<HealEvent>(32);

            Events.Raise(new DamageEvent(_targetId, 10));
            Events.Raise(new HealEvent(_targetId, 5));

            // Immediate delivery finishes before this call returns and overtakes both queued
            // events above. It uses the same listeners and publication metadata.
            Events.RaiseImmediate(new DamageEvent(_targetId, 1));
        }
    }
}
