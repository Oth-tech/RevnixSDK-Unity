using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class TrackTests
    {
        private sealed class FakeHttp : IRevnixHttp
        {
            public readonly List<(string method, string url, string body)> Calls =
                new List<(string, string, string)>();
            public Func<string, Task<RevnixHttpResponse>> Handler = url =>
                Task.FromResult(new RevnixHttpResponse(200, "{\"results\":[]}"));

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                Calls.Add((method, url, jsonBody));
                return Handler(url);
            }

            public void Respond(int status, string body)
                => Handler = url => Task.FromResult(new RevnixHttpResponse(status, body));

            public int CallsTo(string path) => Calls.FindAll(c => c.url.EndsWith(path)).Count;
        }

        private sealed class FakeDelay
        {
            public readonly List<(int ms, TaskCompletionSource<bool> gate)> Pending =
                new List<(int, TaskCompletionSource<bool>)>();

            public Task Delay(int ms)
            {
                var gate = new TaskCompletionSource<bool>();
                Pending.Add((ms, gate));
                return gate.Task;
            }
        }

        private sealed class FakeLifecycle : IRevnixLifecycle
        {
            public Action<RevnixAppState> Handler;

            public Action OnStateChange(Action<RevnixAppState> handler)
            {
                Handler = handler;
                return () => Handler = null;
            }
        }

        private sealed class FullStorage : IRevnixStorage
        {
            public readonly MemoryStorage Inner = new MemoryStorage();
            public string Get(string key) => Inner.Get(key);
            public void Set(string key, string value) => throw new InvalidOperationException("storage full");
            public void Remove(string key) => Inner.Remove(key);
        }

        private sealed class Harness
        {
            public readonly FakeHttp Http = new FakeHttp();
            public readonly FakeDelay Delay = new FakeDelay();
            public readonly MemoryStorage Storage = new MemoryStorage();
            public readonly List<RevnixDiagnostic> Diagnostics = new List<RevnixDiagnostic>();
            public long Now = 1_700_000_000_000L;

            public RevnixClient Client(IRevnixLifecycle lifecycle = null) => new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = Http,
                Storage = Storage,
                Delay = Delay.Delay,
                Now = () => Now,
                Lifecycle = lifecycle,
                OnDiagnostic = d => Diagnostics.Add(d),
            });

            public List<Dictionary<string, object>> Queued()
            {
                var raw = Storage.Get("revnix.eventQueue");
                var result = new List<Dictionary<string, object>>();
                if (raw == null) return result;
                foreach (var item in (List<object>)RevnixJson.Parse(raw))
                {
                    result.Add((Dictionary<string, object>)item);
                }
                return result;
            }

            public List<Dictionary<string, object>> LastBatch(string path)
            {
                var call = Calls(path);
                var result = new List<Dictionary<string, object>>();
                foreach (var item in RevnixJson.GetList(RevnixJson.ParseObject(call[call.Count - 1].body), "events"))
                {
                    result.Add((Dictionary<string, object>)item);
                }
                return result;
            }

            private List<(string method, string url, string body)> Calls(string path)
                => Http.Calls.FindAll(c => c.url.EndsWith(path));
        }

        private static string Results(params int[] statuses)
        {
            var items = new List<string>();
            foreach (var status in statuses) items.Add("{\"status\":" + status + "}");
            return "{\"results\":[" + string.Join(",", items) + "]}";
        }

        private static string EventName(Dictionary<string, object> body) => RevnixJson.GetString(body, "event");

        private static string SeededQueue(int count)
        {
            var items = new List<string>();
            for (var i = 0; i < count; i++)
            {
                items.Add("{\"path\":\"/v1/events\",\"body\":{\"eventId\":\"e" + i + "\",\"event\":\"seed\"}}");
            }
            return "[" + string.Join(",", items) + "]";
        }

        [Test]
        public void ACustomEventIsQueuedThenPostedToEventsWithProperties()
        {
            var h = new Harness();
            var client = h.Client();

            client.Track(
                "level_up",
                new Dictionary<string, object> { ["level"] = 5 }).GetAwaiter().GetResult();
            Assert.AreEqual(0, h.Http.Calls.Count);

            client.FlushEvents().GetAwaiter().GetResult();

            Assert.AreEqual(1, h.Http.Calls.Count);
            Assert.AreEqual("POST", h.Http.Calls[0].method);
            StringAssert.EndsWith("/v1/events", h.Http.Calls[0].url);
            StringAssert.StartsWith("{\"events\":[{", h.Http.Calls[0].body);
            var body = h.LastBatch("/v1/events")[0];
            Assert.AreEqual("level_up", EventName(body));
            Assert.AreEqual(5, RevnixJson.GetNullableLong(RevnixJson.GetObject(body, "properties"), "level"));
            Assert.IsTrue(body.ContainsKey("customerId"));
            Assert.AreEqual(h.Now, RevnixJson.GetLong(body, "occurredAt"));
            Assert.IsFalse(string.IsNullOrEmpty(RevnixJson.GetString(body, "eventId")));
        }

        [Test]
        public void AnInvalidEventNameSendsNothing()
        {
            var h = new Harness();
            var client = h.Client();

            client.Track("Level Up").GetAwaiter().GetResult();
            client.FlushEvents().GetAwaiter().GetResult();

            Assert.AreEqual(0, h.Http.Calls.Count);
            Assert.AreEqual(1, h.Diagnostics.Count);
            Assert.AreEqual("track", h.Diagnostics[0].Op);
        }

        [Test]
        public void UnsupportedPropertyValuesAreDropped()
        {
            var h = new Harness();
            var client = h.Client();

            client.Track(
                "level_up",
                new Dictionary<string, object>
                {
                    ["level"] = 5,
                    ["score"] = double.NaN,
                    ["price"] = 1.5m,
                }).GetAwaiter().GetResult();
            client.FlushEvents().GetAwaiter().GetResult();

            var properties = RevnixJson.GetObject(h.LastBatch("/v1/events")[0], "properties");
            Assert.AreEqual(5, RevnixJson.GetNullableLong(properties, "level"));
            Assert.IsFalse(properties.ContainsKey("score"));
            Assert.IsFalse(properties.ContainsKey("price"));
        }

        [Test]
        public void ThreeTracksFlushAsOneBatchPost()
        {
            var h = new Harness();
            h.Http.Respond(200, Results(201, 201, 201));
            var client = h.Client();
            foreach (var name in new[] { "a", "b", "c" }) client.Track(name).GetAwaiter().GetResult();
            Assert.AreEqual(3, h.Queued().Count);

            client.FlushEvents().GetAwaiter().GetResult();

            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"));
            CollectionAssert.AreEqual(
                new[] { "a", "b", "c" }, h.LastBatch("/v1/events").ConvertAll(EventName));
            Assert.IsNull(h.Storage.Get("revnix.eventQueue"));
        }

        [Test]
        public void TheTimerFlushesTenSecondsAfterTheFirstEventAndTwentyFlushAtOnce()
        {
            var h = new Harness();
            h.Http.Respond(200, Results(201));
            var client = h.Client();

            client.Track("a").GetAwaiter().GetResult();
            client.Track("b").GetAwaiter().GetResult();
            Assert.AreEqual(1, h.Delay.Pending.Count);
            Assert.AreEqual(10_000, h.Delay.Pending[0].ms);
            h.Delay.Pending[0].gate.SetResult(true);
            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"));
            Assert.AreEqual(2, h.LastBatch("/v1/events").Count);

            for (var i = 0; i < 20; i++) client.Track("bulk").GetAwaiter().GetResult();
            Assert.AreEqual(2, h.Http.CallsTo("/v1/events"));
            Assert.AreEqual(20, h.LastBatch("/v1/events").Count);
        }

        [Test]
        public void GoingToTheBackgroundFlushes()
        {
            var h = new Harness();
            var lifecycle = new FakeLifecycle();
            var client = h.Client(lifecycle);
            client.Track("a").GetAwaiter().GetResult();

            lifecycle.Handler(RevnixAppState.Background);

            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"));
            Assert.IsNull(h.Storage.Get("revnix.lastBackgroundAt"));
        }

        [Test]
        public void PaywallEventsQueueWithAnEventIdAndFlushToTheirOwnRoute()
        {
            var h = new Harness();
            h.Http.Respond(200, Results(201));
            var client = h.Client();

            client.LogPaywallEvent(RevnixPaywallEvent.Restore, "view_1", placementKey: "main")
                .GetAwaiter().GetResult();
            Assert.AreEqual(0, h.Http.Calls.Count);

            client.FlushEvents().GetAwaiter().GetResult();

            var events = h.LastBatch("/v1/paywalls/events");
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("restore", EventName(events[0]));
            Assert.AreEqual("view_1", RevnixJson.GetString(events[0], "eventId"));
            Assert.AreEqual(h.Now, RevnixJson.GetLong(events[0], "occurredAt"));
            Assert.AreEqual(0, h.Http.CallsTo("/v1/events"));
        }

        [Test]
        public void RetryableFailureKeepsEventsAndTheNextFlushAfterTheHoldDelivers()
        {
            var h = new Harness();
            h.Http.Respond(503, "{\"error\":\"down\"}");
            var client = h.Client();
            client.Track("a").GetAwaiter().GetResult();
            client.Track("b").GetAwaiter().GetResult();

            client.FlushEvents().GetAwaiter().GetResult();
            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"));
            Assert.AreEqual(2, h.Queued().Count);

            h.Http.Respond(200, Results(201, 201));
            client.FlushEvents().GetAwaiter().GetResult();
            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"), "inside the hold");

            h.Now += 31_000;
            client.FlushEvents().GetAwaiter().GetResult();
            Assert.AreEqual(2, h.Http.CallsTo("/v1/events"));
            CollectionAssert.AreEqual(new[] { "a", "b" }, h.LastBatch("/v1/events").ConvertAll(EventName));
            Assert.IsNull(h.Storage.Get("revnix.eventQueue"));
        }

        [Test]
        public void ANonJsonSuccessKeepsTheEvents()
        {
            var h = new Harness();
            h.Http.Respond(200, "<html>login</html>");
            var client = h.Client();
            client.Track("a").GetAwaiter().GetResult();

            client.FlushEvents().GetAwaiter().GetResult();

            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"));
            Assert.AreEqual(1, h.Queued().Count);
        }

        [Test]
        public void AFlushTriggeredInsideTheHoldRunsWhenTheHoldEnds()
        {
            var h = new Harness();
            h.Http.Respond(503, "{\"error\":\"down\"}");
            h.Storage.Set("revnix.eventQueue", SeededQueue(1));
            var client = h.Client();
            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"), "a leftover queue flushes on construction");
            Assert.IsTrue(h.Diagnostics.Exists(d => d.Message.StartsWith("kept")));

            h.Http.Respond(200, Results(201));
            h.Now += 29_700;
            client.FlushEvents().GetAwaiter().GetResult();
            Assert.AreEqual(1, h.Http.CallsTo("/v1/events"), "inside the hold");
            Assert.AreEqual(1, h.Delay.Pending.Count);
            Assert.AreEqual(300, h.Delay.Pending[0].ms);

            h.Now += 1_000;
            h.Delay.Pending[0].gate.SetResult(true);

            Assert.AreEqual(2, h.Http.CallsTo("/v1/events"));
            Assert.IsNull(h.Storage.Get("revnix.eventQueue"));
        }

        [Test]
        public void ARejectedItemIsDroppedWithADiagnosticAndTheRestAreRemoved()
        {
            var h = new Harness();
            h.Http.Respond(
                200, "{\"results\":[{\"status\":201},{\"status\":400,\"error\":\"bad properties\"},{\"status\":200}]}");
            var client = h.Client();
            foreach (var name in new[] { "a", "b", "c" })
            {
                client.Track(name, eventId: "id_" + name).GetAwaiter().GetResult();
            }

            client.FlushEvents().GetAwaiter().GetResult();

            Assert.IsNull(h.Storage.Get("revnix.eventQueue"));
            var dropped = h.Diagnostics.FindAll(d => d.Op == "eventQueue");
            Assert.AreEqual(1, dropped.Count);
            StringAssert.Contains("id_b", dropped[0].Message);
            StringAssert.Contains("400 bad properties", dropped[0].Message);
        }

        [Test]
        public void AnEventQueuedDuringAFlushSurvivesIt()
        {
            var h = new Harness();
            var response = new TaskCompletionSource<RevnixHttpResponse>();
            h.Http.Handler = url => response.Task;
            var client = h.Client();
            client.Track("a").GetAwaiter().GetResult();

            var flush = client.FlushEvents();
            client.Track("late").GetAwaiter().GetResult();
            response.SetResult(new RevnixHttpResponse(200, Results(201)));
            flush.GetAwaiter().GetResult();

            var queued = h.Queued();
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("late", EventName(RevnixJson.GetObject(queued[0], "body")));
        }

        [Test]
        public void AStorageFailureNeverThrowsIntoTheCaller()
        {
            var http = new FakeHttp();
            var storage = new FullStorage();
            var diagnostics = new List<RevnixDiagnostic>();
            var client = new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = storage,
                OnDiagnostic = d => diagnostics.Add(d),
            });

            client.Track("minting_fails").GetAwaiter().GetResult();
            client.LogPaywallEvent(RevnixPaywallEvent.Restore, "view_1").GetAwaiter().GetResult();
            storage.Inner.Set("revnix.customerId", "cus_1");
            client.Track("queue_write_fails").GetAwaiter().GetResult();

            Assert.AreEqual(3, diagnostics.FindAll(d => d.Op == "eventQueue" && d.Message == "storage full").Count);
            Assert.AreEqual(0, http.Calls.Count);
        }

        [Test]
        public void AFullQueueDropsTheOldestEvent()
        {
            var h = new Harness();
            h.Http.Handler = url => throw new RevnixNetworkException("offline");
            h.Storage.Set("revnix.eventQueue", SeededQueue(1000));
            var client = h.Client();

            client.Track("newest").GetAwaiter().GetResult();

            var queued = h.Queued();
            Assert.AreEqual(1000, queued.Count);
            Assert.AreEqual("e1", RevnixJson.GetString(RevnixJson.GetObject(queued[0], "body"), "eventId"));
            Assert.AreEqual("newest", EventName(RevnixJson.GetObject(queued[999], "body")));
            Assert.IsTrue(h.Diagnostics.Exists(d => d.Message.Contains("queue full")));
        }
    }
}
