using System;
using System.Collections;
using NUnit.Framework;
using QueuedEventBus.Tests.Fixtures;
using UnityEditor;
using UnityEngine.LowLevel;
using UnityEngine.TestTools;

namespace QueuedEventBus.Tests.EditMode
{
    /// <summary>
    /// Verifies real Play-session teardown and restart with domain reload disabled.
    /// </summary>
    public sealed class PlaySessionTests
    {
        private bool _previousOptionsEnabled;
        private EnterPlayModeOptions _previousOptions;

        /// <summary>
        /// Saves the current editor settings and disables domain reload for the session test.
        /// </summary>
        [SetUp]
        public void DisableDomainReload()
        {
            _previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _previousOptions = EditorSettings.enterPlayModeOptions;

            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        }

        /// <summary>
        /// Exits Play Mode if necessary and restores the editor settings saved before the test.
        /// </summary>
        /// <returns>The coroutine that waits for Play Mode to exit before restoring settings.</returns>
        [UnityTearDown]
        public IEnumerator RestoreEditorSettings()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    yield return new ExitPlayMode();
                }
            }
            finally
            {
                EditorSettings.enterPlayModeOptions = _previousOptions;
                EditorSettings.enterPlayModeOptionsEnabled = _previousOptionsEnabled;
            }
        }

        /// <summary>
        /// Verifies metadata retention and fresh subscriptions across two actual Play sessions without domain reload.
        /// </summary>
        /// <returns>The coroutine that enters, checks, and exits both Play sessions.</returns>
        [UnityTest]
        public IEnumerator RealPlaySessionsRetainMetadataAndResetTheDefaultBusWithoutDomainReload()
        {
            var metadata = EventMetadata<ActivityEvent>.Require();
            var firstSessionReceived = 0;
            var secondSessionReceived = 0;
            var firstSessionSequence = 0L;
            var secondSessionSequence = 0L;
            Action<Exception> firstReporter = error => Assert.Fail(error.ToString());

            yield return new EnterPlayMode(expectDomainReload: false);

            AssertReadySession(metadata);
            var firstBus = Events.DefaultBus;
            Events.ExceptionReporter = firstReporter;

            // Deliberately retain this token across Play sessions. Session shutdown
            // must release the subscription even when its owner forgets to dispose it.
            var staleToken = Events.Subscribe<ActivityEvent>((in ActivityEvent message, in EventContext context) =>
            {
                firstSessionReceived++;
                firstSessionSequence = context.Sequence;
            });
            Events.Raise(new ActivityEvent(1));
            var deadline = EditorApplication.timeSinceStartup + 5;
            while (firstSessionReceived == 0 && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(firstSessionReceived, Is.EqualTo(1), "The first session must deliver through its PlayerLoop.");
            Assert.That(firstSessionSequence, Is.EqualTo(1), "A new default bus starts its publication sequence at one.");

            yield return new ExitPlayMode();

            AssertStoppedSession();
            Assert.Throws<ObjectDisposedException>(() => firstBus.Pump());

            yield return new EnterPlayMode(expectDomainReload: false);

            AssertReadySession(metadata);
            Assert.That(Events.DefaultBus, Is.Not.SameAs(firstBus));
            Assert.That(Events.ExceptionReporter, Is.Not.SameAs(firstReporter));

            using (Events.Subscribe<ActivityEvent>((in ActivityEvent message, in EventContext context) =>
            {
                secondSessionReceived++;
                secondSessionSequence = context.Sequence;
            }))
            {
                // An old token must not remove an unrelated subscription on the new bus.
                staleToken.Dispose();
                Events.Raise(new ActivityEvent(2));
                deadline = EditorApplication.timeSinceStartup + 5;
                while (secondSessionReceived == 0 && EditorApplication.timeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(secondSessionReceived, Is.EqualTo(1), "The second session must install automatic delivery again.");
                Assert.That(secondSessionSequence, Is.EqualTo(1), "Publication sequences restart even without domain reload.");
                Assert.That(firstSessionReceived, Is.EqualTo(1), "Subscriptions must not survive Play-session teardown.");
            }

            yield return new ExitPlayMode();

            AssertStoppedSession();
        }

        private static void AssertReadySession(EventDescriptor<ActivityEvent> metadata)
        {
            Assert.That(Events.IsReady, Is.True);
            Assert.That(EventMetadata<ActivityEvent>.Require(), Is.SameAs(metadata));
            Assert.That(CountCallbacks(PlayerLoop.GetCurrentPlayerLoop()), Is.EqualTo(1));
        }

        private static void AssertStoppedSession()
        {
            Assert.That(Events.IsReady, Is.False);
            Assert.That(CountCallbacks(PlayerLoop.GetCurrentPlayerLoop()), Is.Zero);
        }

        private static int CountCallbacks(PlayerLoopSystem system)
        {
            var count = system.type == typeof(EventBusPlayerLoop.Dispatch) ? 1 : 0;
            if (system.subSystemList != null)
            {
                foreach (var child in system.subSystemList)
                {
                    count += CountCallbacks(child);
                }
            }

            return count;
        }
    }
}
