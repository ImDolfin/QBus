using System;
using NUnit.Framework;
using QueuedEventBus.Tests.Fixtures;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace QueuedEventBus.Tests.EditMode
{
    /// <summary>
    /// Verifies default-bus initialization and PlayerLoop callback installation.
    /// </summary>
    public sealed class LifecycleTests
    {
        /// <summary>
        /// Shuts down the default bus and removes its PlayerLoop callback after each test.
        /// </summary>
        [TearDown]
        public void Cleanup()
        {
            Events.Shutdown();
        }

        /// <summary>
        /// Verifies that premature facade access fails before any messages or subscriptions are retained.
        /// </summary>
        [Test]
        public void PrematureDefaultBusUseFailsWithoutMutatingState()
        {
            Events.Reset();
            Assert.Throws<InvalidOperationException>(() => Events.Raise(new ActivityEvent()));
            Assert.Throws<InvalidOperationException>(() => Events.Subscribe<ActivityEvent>(Ignore));
            Assert.Throws<InvalidOperationException>(() => Events.Prewarm<ActivityEvent>(16));
            Events.MarkReady();
            Events.Initialize();
            Assert.That(Events.DefaultBus.Pump(), Is.Zero);
        }

        /// <summary>
        /// Verifies one dispatch callback per session and removal of queued state during teardown.
        /// </summary>
        [Test]
        public void RepeatedSessionsInstallOneCallbackAndResetBusState()
        {
            var count = 0;
            for (var session = 0; session < 3; session++)
            {
                Events.Reset();
                Events.MarkReady();
                Events.Initialize();
                EventBusPlayerLoop.Install();
                Assert.That(CountDispatchCallbacks(PlayerLoop.GetCurrentPlayerLoop()), Is.EqualTo(1));
                AssertDispatchFollowsScriptUpdate(PlayerLoop.GetCurrentPlayerLoop());
                Events.Subscribe<ActivityEvent>((in ActivityEvent item) => count++);
                Events.Raise(new ActivityEvent());
                Assert.That(Events.DefaultBus.Pump(), Is.EqualTo(1));
                Assert.That(count, Is.EqualTo(session + 1));
                Events.Raise(new ActivityEvent());
                Events.Shutdown();
                Assert.That(CountDispatchCallbacks(PlayerLoop.GetCurrentPlayerLoop()), Is.Zero);
            }
        }

        private static void Ignore(in ActivityEvent message)
        {
        }

        private static int CountDispatchCallbacks(PlayerLoopSystem system)
        {
            var count = system.type == typeof(EventBusPlayerLoop.Dispatch) ? 1 : 0;
            if (system.subSystemList != null)
            {
                foreach (var child in system.subSystemList)
                {
                    count += CountDispatchCallbacks(child);
                }
            }

            return count;
        }

        private static void AssertDispatchFollowsScriptUpdate(PlayerLoopSystem system)
        {
            if (system.subSystemList == null)
            {
                return;
            }

            var children = system.subSystemList;
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].type == typeof(EventBusPlayerLoop.Dispatch))
                {
                    Assert.That(i, Is.GreaterThan(0));
                    Assert.That(children[i - 1].type, Is.EqualTo(typeof(Update.ScriptRunBehaviourUpdate)));
                }

                AssertDispatchFollowsScriptUpdate(children[i]);
            }
        }
    }
}
