using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using QueuedEventBus.Tests.Fixtures;

namespace QueuedEventBus.Tests.EditMode
{
    /// <summary>
    /// Verifies facade ownership and teardown while immediate callbacks are active.
    /// </summary>
    public sealed class ImmediateLifecycleTests
    {
        /// <summary>Removes the test session and its PlayerLoop callback.</summary>
        [TearDown]
        public void Cleanup()
        {
            Events.Shutdown();
        }

        /// <summary>Verifies rejected early publications leave the next session untouched.</summary>
        [Test]
        public void PrematureImmediatePublicationDoesNotChangeSessionState()
        {
            Events.Reset();
            Assert.Throws<InvalidOperationException>(() => Events.RaiseImmediate(new ActivityEvent()));
            Initialize();

            long sequence = 0;
            Events.Subscribe<ActivityEvent>((in ActivityEvent message, in EventContext context) =>
            {
                sequence = context.Sequence;
            });
            Events.RaiseImmediate(new ActivityEvent());

            Assert.That(sequence, Is.EqualTo(1));
            Assert.That(Events.DefaultBus.Pump(), Is.Zero);
        }

        /// <summary>Verifies worker-thread calls fail before entering facade dispatch state.</summary>
        [Test]
        public void WorkerThreadImmediatePublicationDoesNotDelayTeardown()
        {
            Initialize();
            var bus = Events.DefaultBus;
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Events.RaiseImmediate(new ActivityEvent());
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            thread.Start();
            thread.Join();

            Assert.That(failure, Is.TypeOf<InvalidOperationException>());
            Events.Shutdown();
            Assert.That(Events.IsReady, Is.False);
            Assert.Throws<ObjectDisposedException>(() => bus.Pump());
        }

        /// <summary>Verifies nested teardown waits for all active immediate and queued callbacks.</summary>
        /// <param name="queuedOuter">Whether the outer publication starts in the automatic pump.</param>
        [TestCase(false)]
        [TestCase(true)]
        public void NestedImmediateShutdownFinishesCapturedCallbacks(bool queuedOuter)
        {
            Initialize();
            var bus = Events.DefaultBus;
            var calls = new List<string>();
            var errors = new List<Exception>();
            var facadeRejected = false;
            var disposalRejected = false;
            Events.ExceptionReporter = errors.Add;
            Events.Subscribe<ActivityEvent>((in ActivityEvent message) =>
            {
                calls.Add("first " + message.Value);

                if (message.Value == 1)
                {
                    Events.RaiseImmediate(new ActivityEvent(2));
                    return;
                }

                Events.Shutdown();
                facadeRejected = Rejects(() => Events.RaiseImmediate(new ActivityEvent(3)));
                disposalRejected = Rejects(bus.Dispose);
            });
            Events.Subscribe<ActivityEvent>((in ActivityEvent message) =>
            {
                calls.Add("last " + message.Value);
            });

            if (queuedOuter)
            {
                Events.Raise(new ActivityEvent(1));
                Events.Dispatch();
            }
            else
            {
                Events.RaiseImmediate(new ActivityEvent(1));
            }

            Assert.That(calls, Is.EqualTo(new[] { "first 1", "first 2", "last 2", "last 1" }));
            Assert.That(errors, Is.Empty);
            Assert.That(facadeRejected, Is.True);
            Assert.That(disposalRejected, Is.True);
            Assert.That(Events.IsReady, Is.False);
            Assert.Throws<ObjectDisposedException>(() => bus.Pump());
        }

        /// <summary>Verifies recursive pumping cannot disturb facade teardown depth.</summary>
        [Test]
        public void RecursiveDispatchFromImmediateListenerLeavesFacadeUsable()
        {
            Initialize();
            var errors = new List<Exception>();
            var rejectionCount = 0;
            var count = 0;
            Events.ExceptionReporter = errors.Add;
            Events.Subscribe<ActivityEvent>((in ActivityEvent message) =>
            {
                if (Rejects(Events.Dispatch))
                {
                    rejectionCount++;
                }

                count++;
            });

            Events.RaiseImmediate(new ActivityEvent());
            Events.Raise(new ActivityEvent());
            Events.Dispatch();

            Assert.That(rejectionCount, Is.EqualTo(2));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(errors, Is.Empty);
            var bus = Events.DefaultBus;
            Events.Shutdown();
            Assert.Throws<ObjectDisposedException>(() => bus.Pump());
        }

        /// <summary>Verifies immediate subscription state and sequence restart every session.</summary>
        [Test]
        public void ImmediatePublicationStartsFreshOnRepeatedSessions()
        {
            var calls = 0;

            for (var session = 0; session < 3; session++)
            {
                Initialize();
                long sequence = 0;
                Events.Subscribe<ActivityEvent>((in ActivityEvent message, in EventContext context) =>
                {
                    calls++;
                    sequence = context.Sequence;
                });
                Events.RaiseImmediate(new ActivityEvent());

                Assert.That(sequence, Is.EqualTo(1));
                Assert.That(calls, Is.EqualTo(session + 1));
                Events.Shutdown();
            }
        }

        private static void Initialize()
        {
            Events.Reset();
            Events.MarkReady();
            Events.Initialize();
        }

        private static bool Rejects(Action operation)
        {
            try
            {
                operation();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
