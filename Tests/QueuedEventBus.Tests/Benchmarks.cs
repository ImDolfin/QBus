using System;
using System.Diagnostics;
using System.Globalization;
using QueuedEventBus.Generated;

#pragma warning disable CS0649 // Benchmark payload fields intentionally retain zero values.

namespace QueuedEventBus.Tests
{
    internal static class Benchmarks
    {
        private const int EventCount = 65536;
        private const int Repetitions = 5;
        private static long _exactDeliveries;
        private static long _contextSink;

        /// <summary>
        /// Runs the benchmark matrix and writes measurements as CSV to standard output.
        /// </summary>
        /// <param name="withContext">Whether callbacks consume publication metadata.</param>
        /// <returns>Zero after all benchmark cases satisfy their delivery and allocation assertions.</returns>
        public static int Run(bool withContext = false)
        {
            Console.WriteLine("# Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Console.WriteLine("# OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            Console.WriteLine("# Processor count: " + Environment.ProcessorCount);
            Console.WriteLine("# Events per sample: " + EventCount + "; measured repetitions: " + Repetitions);
            Console.WriteLine("# Context callbacks: " + withContext);
            Console.WriteLine(
                "event_types,payload_bytes,exact_listeners,category_listeners," +
                "enqueue_ns_per_event,dispatch_ns_per_event,events_per_second,allocated_bytes");

            RunPayload<Payload16>(16, withContext);
            RunPayload<Payload64>(64, withContext);
            RunPayload<Payload256>(256, withContext);
            GC.KeepAlive(_exactDeliveries);
            GC.KeepAlive(_contextSink);
            return 0;
        }

        private static void RunPayload<TPayload>(int payloadBytes, bool withContext)
            where TPayload : struct
        {
            var scenarios = CreateScenarios<TPayload>();
            foreach (var typeCount in new[] { 1, 32, 256 })
            {
                RunCase(scenarios, typeCount, payloadBytes, 0, 0, withContext);
                RunCase(scenarios, typeCount, payloadBytes, 1, 0, withContext);
                RunCase(scenarios, typeCount, payloadBytes, 8, 0, withContext);
                RunCase(scenarios, typeCount, payloadBytes, 0, 1, withContext);
                RunCase(scenarios, typeCount, payloadBytes, 0, 8, withContext);
                RunCase(scenarios, typeCount, payloadBytes, 1, 1, withContext);
                RunCase(scenarios, typeCount, payloadBytes, 4, 4, withContext);
            }
        }

        private static void RunCase(
            IScenario[] scenarios, int typeCount, int payloadBytes, int exactListeners, int categoryListeners,
            bool withContext)
        {
            using (var bus = new EventBus(error => throw error))
            {
                var handler = PrepareCase(bus, scenarios, typeCount, exactListeners, categoryListeners, withContext);
                WarmCase(bus, scenarios, typeCount);

                var exactBefore = _exactDeliveries;
                var categoryBefore = handler.Count;
                var measurements = MeasureSamples(bus, scenarios, typeCount);

                Program.Equal((long)EventCount * Repetitions * exactListeners, _exactDeliveries - exactBefore);
                Program.Equal((long)EventCount * Repetitions * categoryListeners, handler.Count - categoryBefore);
                Program.Equal(0L, measurements.AllocatedBytes);
                WriteMeasurements(typeCount, payloadBytes, exactListeners, categoryListeners, measurements);
            }
        }

        private static CountingHandler PrepareCase(
            EventBus bus, IScenario[] scenarios, int typeCount, int exactListeners, int categoryListeners,
            bool withContext)
        {
            var handler = new CountingHandler();
            for (var i = 0; i < categoryListeners; i++)
            {
                if (withContext)
                {
                    bus.Subscribe<IEvent>((IEventHandlerWithContext<IEvent>)handler);
                }
                else
                {
                    bus.Subscribe<IEvent>((IEventHandler<IEvent>)handler);
                }
            }

            var perType = EventCount / typeCount;
            for (var i = 0; i < typeCount; i++)
            {
                scenarios[i].Prepare(bus, perType, exactListeners, withContext);
            }

            return handler;
        }

        private static void WarmCase(EventBus bus, IScenario[] scenarios, int typeCount)
        {
            // Warm all channels and delivery adapters before either timing or allocation sampling.
            var perType = EventCount / typeCount;
            for (var warm = 0; warm < 3; warm++)
            {
                for (var i = 0; i < typeCount; i++)
                {
                    scenarios[i].Enqueue(bus, perType);
                }

                Program.Equal(EventCount, bus.Pump());
            }
        }

        private static (long EnqueueTicks, long DispatchTicks, long AllocatedBytes) MeasureSamples(
            EventBus bus, IScenario[] scenarios, int typeCount)
        {
            var perType = EventCount / typeCount;
            long enqueueTicks = 0;
            long dispatchTicks = 0;
            long allocated = 0;

            for (var repetition = 0; repetition < Repetitions; repetition++)
            {
                var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < typeCount; i++)
                {
                    scenarios[i].Enqueue(bus, perType);
                }

                var enqueued = Stopwatch.GetTimestamp();
                var delivered = bus.Pump();
                var end = Stopwatch.GetTimestamp();
                allocated += GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

                // Assertions must stay outside the measured enqueue/dispatch interval.
                Program.Equal(EventCount, delivered);
                enqueueTicks += enqueued - start;
                dispatchTicks += end - enqueued;
            }

            return (enqueueTicks, dispatchTicks, allocated);
        }

        private static void WriteMeasurements(
            int typeCount, int payloadBytes, int exactListeners, int categoryListeners,
            (long EnqueueTicks, long DispatchTicks, long AllocatedBytes) measurements)
        {
            var events = (double)EventCount * Repetitions;
            var enqueueNs = measurements.EnqueueTicks * 1e9 / Stopwatch.Frequency / events;
            var dispatchNs = measurements.DispatchTicks * 1e9 / Stopwatch.Frequency / events;
            var throughput = events * Stopwatch.Frequency / (measurements.EnqueueTicks + measurements.DispatchTicks);

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4:F2},{5:F2},{6:F0},{7}",
                typeCount, payloadBytes, exactListeners, categoryListeners,
                enqueueNs, dispatchNs, throughput, measurements.AllocatedBytes));
        }

        private interface IScenario
        {
            /// <summary>Reserves channel capacity and installs exact listeners.</summary>
            /// <param name="bus">The bus being measured.</param>
            /// <param name="capacity">The required message capacity.</param>
            /// <param name="listeners">The number of exact listeners to install.</param>
            /// <param name="withContext">Whether the exact listeners consume publication metadata.</param>
            void Prepare(EventBus bus, int capacity, int listeners, bool withContext);

            /// <summary>Publishes a batch of this scenario's concrete event type.</summary>
            /// <param name="bus">The bus receiving the messages.</param>
            /// <param name="count">The number of messages to enqueue.</param>
            void Enqueue(EventBus bus, int count);
        }

        private sealed class Scenario<TPayload, TTag> : IScenario where TPayload : struct where TTag : struct
        {
            /// <summary>
            /// Registers the concrete payload/tag combination used by this scenario.
            /// </summary>
            public Scenario()
            {
                // Closed generic fixture types create many distinct channels without code generation.
                GeneratedEventCatalog.RegisterStruct<BenchmarkEvent<TPayload, TTag>>(
                    GeneratedRoutes.Create<BenchmarkEvent<TPayload, TTag>, BenchmarkEvent<TPayload, TTag>>(),
                    GeneratedRoutes.Create<BenchmarkEvent<TPayload, TTag>, IEvent>());
            }

            /// <summary>
            /// Prewarms this event channel and installs its exact subscriptions.
            /// </summary>
            /// <param name="bus">The bus being measured.</param>
            /// <param name="capacity">The number of messages reserved in this channel.</param>
            /// <param name="listeners">The number of exact subscribers to install.</param>
            /// <param name="withContext">Whether the exact subscribers consume publication metadata.</param>
            public void Prepare(EventBus bus, int capacity, int listeners, bool withContext)
            {
                bus.Prewarm<BenchmarkEvent<TPayload, TTag>>(capacity);

                for (var i = 0; i < listeners; i++)
                {
                    if (withContext)
                    {
                        bus.Subscribe<BenchmarkEvent<TPayload, TTag>>(ConsumeContext);
                    }
                    else
                    {
                        bus.Subscribe<BenchmarkEvent<TPayload, TTag>>(Consume);
                    }
                }
            }

            /// <summary>
            /// Enqueues the requested number of default payloads for this scenario.
            /// </summary>
            /// <param name="bus">The bus receiving the messages.</param>
            /// <param name="count">The number of messages to publish.</param>
            public void Enqueue(EventBus bus, int count)
            {
                var message = default(BenchmarkEvent<TPayload, TTag>);
                for (var i = 0; i < count; i++)
                {
                    bus.Raise(in message);
                }
            }

            private static void Consume(in BenchmarkEvent<TPayload, TTag> message)
            {
                _exactDeliveries++;
            }

            private static void ConsumeContext(in BenchmarkEvent<TPayload, TTag> message, in EventContext context)
            {
                _exactDeliveries++;
                _contextSink = context.Sequence ^ context.RaisedTimestamp;
            }
        }

        private sealed class CountingHandler : IEventHandler<IEvent>, IEventHandlerWithContext<IEvent>
        {
            /// <summary>
            /// The number of callback invocations observed by this handler.
            /// </summary>
            public long Count;

            /// <summary>
            /// Counts one delivered event without reading or boxing its payload.
            /// </summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered event passed by readonly reference.</param>
            public void Handle<T>(in T message)
                where T : IEvent
            {
                Count++;
            }

            /// <summary>Counts delivery and reads metadata while retaining the concrete payload type.</summary>
            /// <typeparam name="T">The concrete delivered event type.</typeparam>
            /// <param name="message">The delivered payload passed by readonly reference.</param>
            /// <param name="context">The raise-time metadata passed by readonly reference.</param>
            public void Handle<T>(in T message, in EventContext context)
                where T : IEvent
            {
                Count++;
                _contextSink = context.Sequence ^ context.RaisedTimestamp;
            }
        }

        private readonly struct BenchmarkEvent<TPayload, TTag> : IEvent where TPayload : struct where TTag : struct
        {
            /// <summary>
            /// The payload stored in this event.
            /// </summary>
            public readonly TPayload Payload;
        }

        private struct Payload16
        {
            /// <summary>
            /// The first fixed-size payload section.
            /// </summary>
            public long A;

            /// <summary>
            /// The second fixed-size payload section.
            /// </summary>
            public long B;
        }

        private struct Payload64
        {
            /// <summary>
            /// The first fixed-size payload section.
            /// </summary>
            public Payload16 A;

            /// <summary>
            /// The second fixed-size payload section.
            /// </summary>
            public Payload16 B;

            /// <summary>
            /// The third fixed-size payload section.
            /// </summary>
            public Payload16 C;

            /// <summary>
            /// The fourth fixed-size payload section.
            /// </summary>
            public Payload16 D;
        }

        private struct Payload256
        {
            /// <summary>
            /// The first fixed-size payload section.
            /// </summary>
            public Payload64 A;

            /// <summary>
            /// The second fixed-size payload section.
            /// </summary>
            public Payload64 B;

            /// <summary>
            /// The third fixed-size payload section.
            /// </summary>
            public Payload64 C;

            /// <summary>
            /// The fourth fixed-size payload section.
            /// </summary>
            public Payload64 D;
        }

        #region Benchmark type identities

        // Explicit closed generic references give the benchmark 256 distinct event types.
        // Keep this mechanical fixture list separate from the measurement algorithm.
        private static IScenario[] CreateScenarios<TPayload>()
            where TPayload : struct
        {
            return new IScenario[]
            {
                new Scenario<TPayload, Tag000>(),
                new Scenario<TPayload, Tag001>(),
                new Scenario<TPayload, Tag002>(),
                new Scenario<TPayload, Tag003>(),
                new Scenario<TPayload, Tag004>(),
                new Scenario<TPayload, Tag005>(),
                new Scenario<TPayload, Tag006>(),
                new Scenario<TPayload, Tag007>(),
                new Scenario<TPayload, Tag008>(),
                new Scenario<TPayload, Tag009>(),
                new Scenario<TPayload, Tag010>(),
                new Scenario<TPayload, Tag011>(),
                new Scenario<TPayload, Tag012>(),
                new Scenario<TPayload, Tag013>(),
                new Scenario<TPayload, Tag014>(),
                new Scenario<TPayload, Tag015>(),
                new Scenario<TPayload, Tag016>(),
                new Scenario<TPayload, Tag017>(),
                new Scenario<TPayload, Tag018>(),
                new Scenario<TPayload, Tag019>(),
                new Scenario<TPayload, Tag020>(),
                new Scenario<TPayload, Tag021>(),
                new Scenario<TPayload, Tag022>(),
                new Scenario<TPayload, Tag023>(),
                new Scenario<TPayload, Tag024>(),
                new Scenario<TPayload, Tag025>(),
                new Scenario<TPayload, Tag026>(),
                new Scenario<TPayload, Tag027>(),
                new Scenario<TPayload, Tag028>(),
                new Scenario<TPayload, Tag029>(),
                new Scenario<TPayload, Tag030>(),
                new Scenario<TPayload, Tag031>(),
                new Scenario<TPayload, Tag032>(),
                new Scenario<TPayload, Tag033>(),
                new Scenario<TPayload, Tag034>(),
                new Scenario<TPayload, Tag035>(),
                new Scenario<TPayload, Tag036>(),
                new Scenario<TPayload, Tag037>(),
                new Scenario<TPayload, Tag038>(),
                new Scenario<TPayload, Tag039>(),
                new Scenario<TPayload, Tag040>(),
                new Scenario<TPayload, Tag041>(),
                new Scenario<TPayload, Tag042>(),
                new Scenario<TPayload, Tag043>(),
                new Scenario<TPayload, Tag044>(),
                new Scenario<TPayload, Tag045>(),
                new Scenario<TPayload, Tag046>(),
                new Scenario<TPayload, Tag047>(),
                new Scenario<TPayload, Tag048>(),
                new Scenario<TPayload, Tag049>(),
                new Scenario<TPayload, Tag050>(),
                new Scenario<TPayload, Tag051>(),
                new Scenario<TPayload, Tag052>(),
                new Scenario<TPayload, Tag053>(),
                new Scenario<TPayload, Tag054>(),
                new Scenario<TPayload, Tag055>(),
                new Scenario<TPayload, Tag056>(),
                new Scenario<TPayload, Tag057>(),
                new Scenario<TPayload, Tag058>(),
                new Scenario<TPayload, Tag059>(),
                new Scenario<TPayload, Tag060>(),
                new Scenario<TPayload, Tag061>(),
                new Scenario<TPayload, Tag062>(),
                new Scenario<TPayload, Tag063>(),
                new Scenario<TPayload, Tag064>(),
                new Scenario<TPayload, Tag065>(),
                new Scenario<TPayload, Tag066>(),
                new Scenario<TPayload, Tag067>(),
                new Scenario<TPayload, Tag068>(),
                new Scenario<TPayload, Tag069>(),
                new Scenario<TPayload, Tag070>(),
                new Scenario<TPayload, Tag071>(),
                new Scenario<TPayload, Tag072>(),
                new Scenario<TPayload, Tag073>(),
                new Scenario<TPayload, Tag074>(),
                new Scenario<TPayload, Tag075>(),
                new Scenario<TPayload, Tag076>(),
                new Scenario<TPayload, Tag077>(),
                new Scenario<TPayload, Tag078>(),
                new Scenario<TPayload, Tag079>(),
                new Scenario<TPayload, Tag080>(),
                new Scenario<TPayload, Tag081>(),
                new Scenario<TPayload, Tag082>(),
                new Scenario<TPayload, Tag083>(),
                new Scenario<TPayload, Tag084>(),
                new Scenario<TPayload, Tag085>(),
                new Scenario<TPayload, Tag086>(),
                new Scenario<TPayload, Tag087>(),
                new Scenario<TPayload, Tag088>(),
                new Scenario<TPayload, Tag089>(),
                new Scenario<TPayload, Tag090>(),
                new Scenario<TPayload, Tag091>(),
                new Scenario<TPayload, Tag092>(),
                new Scenario<TPayload, Tag093>(),
                new Scenario<TPayload, Tag094>(),
                new Scenario<TPayload, Tag095>(),
                new Scenario<TPayload, Tag096>(),
                new Scenario<TPayload, Tag097>(),
                new Scenario<TPayload, Tag098>(),
                new Scenario<TPayload, Tag099>(),
                new Scenario<TPayload, Tag100>(),
                new Scenario<TPayload, Tag101>(),
                new Scenario<TPayload, Tag102>(),
                new Scenario<TPayload, Tag103>(),
                new Scenario<TPayload, Tag104>(),
                new Scenario<TPayload, Tag105>(),
                new Scenario<TPayload, Tag106>(),
                new Scenario<TPayload, Tag107>(),
                new Scenario<TPayload, Tag108>(),
                new Scenario<TPayload, Tag109>(),
                new Scenario<TPayload, Tag110>(),
                new Scenario<TPayload, Tag111>(),
                new Scenario<TPayload, Tag112>(),
                new Scenario<TPayload, Tag113>(),
                new Scenario<TPayload, Tag114>(),
                new Scenario<TPayload, Tag115>(),
                new Scenario<TPayload, Tag116>(),
                new Scenario<TPayload, Tag117>(),
                new Scenario<TPayload, Tag118>(),
                new Scenario<TPayload, Tag119>(),
                new Scenario<TPayload, Tag120>(),
                new Scenario<TPayload, Tag121>(),
                new Scenario<TPayload, Tag122>(),
                new Scenario<TPayload, Tag123>(),
                new Scenario<TPayload, Tag124>(),
                new Scenario<TPayload, Tag125>(),
                new Scenario<TPayload, Tag126>(),
                new Scenario<TPayload, Tag127>(),
                new Scenario<TPayload, Tag128>(),
                new Scenario<TPayload, Tag129>(),
                new Scenario<TPayload, Tag130>(),
                new Scenario<TPayload, Tag131>(),
                new Scenario<TPayload, Tag132>(),
                new Scenario<TPayload, Tag133>(),
                new Scenario<TPayload, Tag134>(),
                new Scenario<TPayload, Tag135>(),
                new Scenario<TPayload, Tag136>(),
                new Scenario<TPayload, Tag137>(),
                new Scenario<TPayload, Tag138>(),
                new Scenario<TPayload, Tag139>(),
                new Scenario<TPayload, Tag140>(),
                new Scenario<TPayload, Tag141>(),
                new Scenario<TPayload, Tag142>(),
                new Scenario<TPayload, Tag143>(),
                new Scenario<TPayload, Tag144>(),
                new Scenario<TPayload, Tag145>(),
                new Scenario<TPayload, Tag146>(),
                new Scenario<TPayload, Tag147>(),
                new Scenario<TPayload, Tag148>(),
                new Scenario<TPayload, Tag149>(),
                new Scenario<TPayload, Tag150>(),
                new Scenario<TPayload, Tag151>(),
                new Scenario<TPayload, Tag152>(),
                new Scenario<TPayload, Tag153>(),
                new Scenario<TPayload, Tag154>(),
                new Scenario<TPayload, Tag155>(),
                new Scenario<TPayload, Tag156>(),
                new Scenario<TPayload, Tag157>(),
                new Scenario<TPayload, Tag158>(),
                new Scenario<TPayload, Tag159>(),
                new Scenario<TPayload, Tag160>(),
                new Scenario<TPayload, Tag161>(),
                new Scenario<TPayload, Tag162>(),
                new Scenario<TPayload, Tag163>(),
                new Scenario<TPayload, Tag164>(),
                new Scenario<TPayload, Tag165>(),
                new Scenario<TPayload, Tag166>(),
                new Scenario<TPayload, Tag167>(),
                new Scenario<TPayload, Tag168>(),
                new Scenario<TPayload, Tag169>(),
                new Scenario<TPayload, Tag170>(),
                new Scenario<TPayload, Tag171>(),
                new Scenario<TPayload, Tag172>(),
                new Scenario<TPayload, Tag173>(),
                new Scenario<TPayload, Tag174>(),
                new Scenario<TPayload, Tag175>(),
                new Scenario<TPayload, Tag176>(),
                new Scenario<TPayload, Tag177>(),
                new Scenario<TPayload, Tag178>(),
                new Scenario<TPayload, Tag179>(),
                new Scenario<TPayload, Tag180>(),
                new Scenario<TPayload, Tag181>(),
                new Scenario<TPayload, Tag182>(),
                new Scenario<TPayload, Tag183>(),
                new Scenario<TPayload, Tag184>(),
                new Scenario<TPayload, Tag185>(),
                new Scenario<TPayload, Tag186>(),
                new Scenario<TPayload, Tag187>(),
                new Scenario<TPayload, Tag188>(),
                new Scenario<TPayload, Tag189>(),
                new Scenario<TPayload, Tag190>(),
                new Scenario<TPayload, Tag191>(),
                new Scenario<TPayload, Tag192>(),
                new Scenario<TPayload, Tag193>(),
                new Scenario<TPayload, Tag194>(),
                new Scenario<TPayload, Tag195>(),
                new Scenario<TPayload, Tag196>(),
                new Scenario<TPayload, Tag197>(),
                new Scenario<TPayload, Tag198>(),
                new Scenario<TPayload, Tag199>(),
                new Scenario<TPayload, Tag200>(),
                new Scenario<TPayload, Tag201>(),
                new Scenario<TPayload, Tag202>(),
                new Scenario<TPayload, Tag203>(),
                new Scenario<TPayload, Tag204>(),
                new Scenario<TPayload, Tag205>(),
                new Scenario<TPayload, Tag206>(),
                new Scenario<TPayload, Tag207>(),
                new Scenario<TPayload, Tag208>(),
                new Scenario<TPayload, Tag209>(),
                new Scenario<TPayload, Tag210>(),
                new Scenario<TPayload, Tag211>(),
                new Scenario<TPayload, Tag212>(),
                new Scenario<TPayload, Tag213>(),
                new Scenario<TPayload, Tag214>(),
                new Scenario<TPayload, Tag215>(),
                new Scenario<TPayload, Tag216>(),
                new Scenario<TPayload, Tag217>(),
                new Scenario<TPayload, Tag218>(),
                new Scenario<TPayload, Tag219>(),
                new Scenario<TPayload, Tag220>(),
                new Scenario<TPayload, Tag221>(),
                new Scenario<TPayload, Tag222>(),
                new Scenario<TPayload, Tag223>(),
                new Scenario<TPayload, Tag224>(),
                new Scenario<TPayload, Tag225>(),
                new Scenario<TPayload, Tag226>(),
                new Scenario<TPayload, Tag227>(),
                new Scenario<TPayload, Tag228>(),
                new Scenario<TPayload, Tag229>(),
                new Scenario<TPayload, Tag230>(),
                new Scenario<TPayload, Tag231>(),
                new Scenario<TPayload, Tag232>(),
                new Scenario<TPayload, Tag233>(),
                new Scenario<TPayload, Tag234>(),
                new Scenario<TPayload, Tag235>(),
                new Scenario<TPayload, Tag236>(),
                new Scenario<TPayload, Tag237>(),
                new Scenario<TPayload, Tag238>(),
                new Scenario<TPayload, Tag239>(),
                new Scenario<TPayload, Tag240>(),
                new Scenario<TPayload, Tag241>(),
                new Scenario<TPayload, Tag242>(),
                new Scenario<TPayload, Tag243>(),
                new Scenario<TPayload, Tag244>(),
                new Scenario<TPayload, Tag245>(),
                new Scenario<TPayload, Tag246>(),
                new Scenario<TPayload, Tag247>(),
                new Scenario<TPayload, Tag248>(),
                new Scenario<TPayload, Tag249>(),
                new Scenario<TPayload, Tag250>(),
                new Scenario<TPayload, Tag251>(),
                new Scenario<TPayload, Tag252>(),
                new Scenario<TPayload, Tag253>(),
                new Scenario<TPayload, Tag254>(),
                new Scenario<TPayload, Tag255>(),
            };
        }

        private struct Tag000
        {
        }

        private struct Tag001
        {
        }

        private struct Tag002
        {
        }

        private struct Tag003
        {
        }

        private struct Tag004
        {
        }

        private struct Tag005
        {
        }

        private struct Tag006
        {
        }

        private struct Tag007
        {
        }

        private struct Tag008
        {
        }

        private struct Tag009
        {
        }

        private struct Tag010
        {
        }

        private struct Tag011
        {
        }

        private struct Tag012
        {
        }

        private struct Tag013
        {
        }

        private struct Tag014
        {
        }

        private struct Tag015
        {
        }

        private struct Tag016
        {
        }

        private struct Tag017
        {
        }

        private struct Tag018
        {
        }

        private struct Tag019
        {
        }

        private struct Tag020
        {
        }

        private struct Tag021
        {
        }

        private struct Tag022
        {
        }

        private struct Tag023
        {
        }

        private struct Tag024
        {
        }

        private struct Tag025
        {
        }

        private struct Tag026
        {
        }

        private struct Tag027
        {
        }

        private struct Tag028
        {
        }

        private struct Tag029
        {
        }

        private struct Tag030
        {
        }

        private struct Tag031
        {
        }

        private struct Tag032
        {
        }

        private struct Tag033
        {
        }

        private struct Tag034
        {
        }

        private struct Tag035
        {
        }

        private struct Tag036
        {
        }

        private struct Tag037
        {
        }

        private struct Tag038
        {
        }

        private struct Tag039
        {
        }

        private struct Tag040
        {
        }

        private struct Tag041
        {
        }

        private struct Tag042
        {
        }

        private struct Tag043
        {
        }

        private struct Tag044
        {
        }

        private struct Tag045
        {
        }

        private struct Tag046
        {
        }

        private struct Tag047
        {
        }

        private struct Tag048
        {
        }

        private struct Tag049
        {
        }

        private struct Tag050
        {
        }

        private struct Tag051
        {
        }

        private struct Tag052
        {
        }

        private struct Tag053
        {
        }

        private struct Tag054
        {
        }

        private struct Tag055
        {
        }

        private struct Tag056
        {
        }

        private struct Tag057
        {
        }

        private struct Tag058
        {
        }

        private struct Tag059
        {
        }

        private struct Tag060
        {
        }

        private struct Tag061
        {
        }

        private struct Tag062
        {
        }

        private struct Tag063
        {
        }

        private struct Tag064
        {
        }

        private struct Tag065
        {
        }

        private struct Tag066
        {
        }

        private struct Tag067
        {
        }

        private struct Tag068
        {
        }

        private struct Tag069
        {
        }

        private struct Tag070
        {
        }

        private struct Tag071
        {
        }

        private struct Tag072
        {
        }

        private struct Tag073
        {
        }

        private struct Tag074
        {
        }

        private struct Tag075
        {
        }

        private struct Tag076
        {
        }

        private struct Tag077
        {
        }

        private struct Tag078
        {
        }

        private struct Tag079
        {
        }

        private struct Tag080
        {
        }

        private struct Tag081
        {
        }

        private struct Tag082
        {
        }

        private struct Tag083
        {
        }

        private struct Tag084
        {
        }

        private struct Tag085
        {
        }

        private struct Tag086
        {
        }

        private struct Tag087
        {
        }

        private struct Tag088
        {
        }

        private struct Tag089
        {
        }

        private struct Tag090
        {
        }

        private struct Tag091
        {
        }

        private struct Tag092
        {
        }

        private struct Tag093
        {
        }

        private struct Tag094
        {
        }

        private struct Tag095
        {
        }

        private struct Tag096
        {
        }

        private struct Tag097
        {
        }

        private struct Tag098
        {
        }

        private struct Tag099
        {
        }

        private struct Tag100
        {
        }

        private struct Tag101
        {
        }

        private struct Tag102
        {
        }

        private struct Tag103
        {
        }

        private struct Tag104
        {
        }

        private struct Tag105
        {
        }

        private struct Tag106
        {
        }

        private struct Tag107
        {
        }

        private struct Tag108
        {
        }

        private struct Tag109
        {
        }

        private struct Tag110
        {
        }

        private struct Tag111
        {
        }

        private struct Tag112
        {
        }

        private struct Tag113
        {
        }

        private struct Tag114
        {
        }

        private struct Tag115
        {
        }

        private struct Tag116
        {
        }

        private struct Tag117
        {
        }

        private struct Tag118
        {
        }

        private struct Tag119
        {
        }

        private struct Tag120
        {
        }

        private struct Tag121
        {
        }

        private struct Tag122
        {
        }

        private struct Tag123
        {
        }

        private struct Tag124
        {
        }

        private struct Tag125
        {
        }

        private struct Tag126
        {
        }

        private struct Tag127
        {
        }

        private struct Tag128
        {
        }

        private struct Tag129
        {
        }

        private struct Tag130
        {
        }

        private struct Tag131
        {
        }

        private struct Tag132
        {
        }

        private struct Tag133
        {
        }

        private struct Tag134
        {
        }

        private struct Tag135
        {
        }

        private struct Tag136
        {
        }

        private struct Tag137
        {
        }

        private struct Tag138
        {
        }

        private struct Tag139
        {
        }

        private struct Tag140
        {
        }

        private struct Tag141
        {
        }

        private struct Tag142
        {
        }

        private struct Tag143
        {
        }

        private struct Tag144
        {
        }

        private struct Tag145
        {
        }

        private struct Tag146
        {
        }

        private struct Tag147
        {
        }

        private struct Tag148
        {
        }

        private struct Tag149
        {
        }

        private struct Tag150
        {
        }

        private struct Tag151
        {
        }

        private struct Tag152
        {
        }

        private struct Tag153
        {
        }

        private struct Tag154
        {
        }

        private struct Tag155
        {
        }

        private struct Tag156
        {
        }

        private struct Tag157
        {
        }

        private struct Tag158
        {
        }

        private struct Tag159
        {
        }

        private struct Tag160
        {
        }

        private struct Tag161
        {
        }

        private struct Tag162
        {
        }

        private struct Tag163
        {
        }

        private struct Tag164
        {
        }

        private struct Tag165
        {
        }

        private struct Tag166
        {
        }

        private struct Tag167
        {
        }

        private struct Tag168
        {
        }

        private struct Tag169
        {
        }

        private struct Tag170
        {
        }

        private struct Tag171
        {
        }

        private struct Tag172
        {
        }

        private struct Tag173
        {
        }

        private struct Tag174
        {
        }

        private struct Tag175
        {
        }

        private struct Tag176
        {
        }

        private struct Tag177
        {
        }

        private struct Tag178
        {
        }

        private struct Tag179
        {
        }

        private struct Tag180
        {
        }

        private struct Tag181
        {
        }

        private struct Tag182
        {
        }

        private struct Tag183
        {
        }

        private struct Tag184
        {
        }

        private struct Tag185
        {
        }

        private struct Tag186
        {
        }

        private struct Tag187
        {
        }

        private struct Tag188
        {
        }

        private struct Tag189
        {
        }

        private struct Tag190
        {
        }

        private struct Tag191
        {
        }

        private struct Tag192
        {
        }

        private struct Tag193
        {
        }

        private struct Tag194
        {
        }

        private struct Tag195
        {
        }

        private struct Tag196
        {
        }

        private struct Tag197
        {
        }

        private struct Tag198
        {
        }

        private struct Tag199
        {
        }

        private struct Tag200
        {
        }

        private struct Tag201
        {
        }

        private struct Tag202
        {
        }

        private struct Tag203
        {
        }

        private struct Tag204
        {
        }

        private struct Tag205
        {
        }

        private struct Tag206
        {
        }

        private struct Tag207
        {
        }

        private struct Tag208
        {
        }

        private struct Tag209
        {
        }

        private struct Tag210
        {
        }

        private struct Tag211
        {
        }

        private struct Tag212
        {
        }

        private struct Tag213
        {
        }

        private struct Tag214
        {
        }

        private struct Tag215
        {
        }

        private struct Tag216
        {
        }

        private struct Tag217
        {
        }

        private struct Tag218
        {
        }

        private struct Tag219
        {
        }

        private struct Tag220
        {
        }

        private struct Tag221
        {
        }

        private struct Tag222
        {
        }

        private struct Tag223
        {
        }

        private struct Tag224
        {
        }

        private struct Tag225
        {
        }

        private struct Tag226
        {
        }

        private struct Tag227
        {
        }

        private struct Tag228
        {
        }

        private struct Tag229
        {
        }

        private struct Tag230
        {
        }

        private struct Tag231
        {
        }

        private struct Tag232
        {
        }

        private struct Tag233
        {
        }

        private struct Tag234
        {
        }

        private struct Tag235
        {
        }

        private struct Tag236
        {
        }

        private struct Tag237
        {
        }

        private struct Tag238
        {
        }

        private struct Tag239
        {
        }

        private struct Tag240
        {
        }

        private struct Tag241
        {
        }

        private struct Tag242
        {
        }

        private struct Tag243
        {
        }

        private struct Tag244
        {
        }

        private struct Tag245
        {
        }

        private struct Tag246
        {
        }

        private struct Tag247
        {
        }

        private struct Tag248
        {
        }

        private struct Tag249
        {
        }

        private struct Tag250
        {
        }

        private struct Tag251
        {
        }

        private struct Tag252
        {
        }

        private struct Tag253
        {
        }

        private struct Tag254
        {
        }

        private struct Tag255
        {
        }

        #endregion
    }
}
