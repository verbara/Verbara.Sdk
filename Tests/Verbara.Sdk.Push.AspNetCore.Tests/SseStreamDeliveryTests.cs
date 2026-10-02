namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Push.Diagnostics;

/// <summary>
/// The classes that assert on process-wide state — <c>TaskScheduler.UnobservedTaskException</c> and the
/// stream's drop metric, read by meter name — run alone (design D7).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SseProcessWideStateGroup
{
    public const string Name = "SSE process-wide state";
}

/// <summary>Frame helpers shared by the delivery and bound tests.</summary>
internal static class LoadFrames
{
    public const string Prefix = "load.";
    public const string Suffix = ".evt";

    public static string Topic(int i) => Prefix + i.ToString(CultureInfo.InvariantCulture) + Suffix;

    public static string Correlation(int i, string pad) => i.ToString(CultureInfo.InvariantCulture) + ":" + pad;

    /// <summary>The sequence number of a load event frame, or -1 when the frame is not one.</summary>
    public static int Sequence(SseFrame frame)
    {
        var name = frame.EventName;
        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal))
            return -1;
        return int.TryParse(name.AsSpan(Prefix.Length, name.Length - Prefix.Length - Suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1;
    }

    /// <summary>Whether a load frame is whole: one event line, one data line of JSON naming the same topic and padding.</summary>
    public static bool IsWellFormed(SseFrame frame, string pad)
    {
        var i = Sequence(frame);
        if (i < 0 || frame.Lines.Count != 2 || frame.Data is not { } data)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var metadata = doc.RootElement.GetProperty("metadata");
            return metadata.GetProperty("topicPath").GetString() == frame.EventName
                && metadata.GetProperty("correlationId").GetString() == Correlation(i, pad);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>
/// Spec <c>push-sse-stream-delivery</c>, requirement <i>The stream works on a host that forbids synchronous
/// I/O</i>: default Kestrel serves 200 with the headers before any frame, also for a host set up with
/// <c>AddVerbaraPush()</c> only.
/// </summary>
public sealed class SseDefaultKestrelTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly string[] Broadcasts = ["queue.1.updated", "billing.invoice.created", "agent.7.state", "cluster.node.down"];

    public static TheoryData<PushRegistration, bool> Hosts() => new()
    {
        { PushRegistration.AspNetCore, false },
        { PushRegistration.AspNetCore, true },
        { PushRegistration.PushOnly, false },
        { PushRegistration.PushOnly, true },
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Stream_ShouldServe200WithHeadersBeforeAnyFrame_WhenHostUsesDefaultKestrelSettings(PushRegistration registration, bool allowSynchronousIO)
    {
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            Registration = registration,
            BusCapacity = 64,
        });

        // Nothing is published until the headers are read; the shipped 15 s heartbeat is longer than the bound.
        using var abort = new CancellationTokenSource();
        var responseTask = host.OpenStreamAsync(string.Empty, abort.Token);
        var headersArrived = await Task.WhenAny(responseTask, Task.Delay(Bound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
        using var response = headersArrived ? await responseTask : null;

        var subscribed = response?.StatusCode == HttpStatusCode.OK
            && await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);
        foreach (var topic in Broadcasts)
            await host.PublishAsync(topic);

        var received = new List<string>();
        if (response?.StatusCode == HttpStatusCode.OK)
        {
            using var reader = new SseReader(await response.Content.ReadAsStreamAsync(abort.Token));
            await reader.ReadUntilAsync(f => f.EventName == Broadcasts[^1], Bound);
            received.AddRange(reader.Frames.Select(static f => f.EventName).OfType<string>());
        }

        await abort.CancelAsync();

        using (new AssertionScope($"{registration} (AllowSynchronousIO={allowSynchronousIO})"))
        {
            headersArrived.Should().BeTrue("the headers reach the client as soon as the request is admitted, before any frame");
            response?.StatusCode.Should().Be(HttpStatusCode.OK);
            response?.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
            subscribed.Should().BeTrue("the admitted stream subscribes to the bus");
            received.Should().Equal(Broadcasts, "the client receives the four broadcast events");
            host.LoggedExceptions.Select(static l => l.Exception!.GetType().Name + ": " + l.Exception.Message)
                .Should().BeEmpty("the host logs no exception");
        }
    }
}

/// <summary>
/// Spec <c>push-sse-stream-delivery</c>, requirements <i>Frames are written whole and one at a time</i> and
/// <i>The heartbeat keeps running under a slow reader</i>.
/// </summary>
public sealed class SseFrameTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    public static TheoryData<bool> Settings() => new() { false, true };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldWriteOneEventLineAndOneDataLine_WhenTopicPathCarriesCrLf(bool allowSynchronousIO)
    {
        const string topic = "queue.a\r\ndata: x.updated";
        const string sentinel = "queue.9.sentinel";
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });

        using var abort = new CancellationTokenSource();
        var responseTask = host.OpenStreamAsync(string.Empty, abort.Token);
        var headersArrived = await Task.WhenAny(responseTask, Task.Delay(Bound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
        using var response = headersArrived ? await responseTask : null;
        var subscribed = response?.StatusCode == HttpStatusCode.OK
            && await host.Bus.Subscribers.WaitUntilAsync(static n => n >= 1, Bound);

        await host.PublishAsync(topic);
        await host.PublishAsync(sentinel);

        IReadOnlyList<SseFrame> frames = [];
        if (subscribed)
        {
            using var reader = new SseReader(await response!.Content.ReadAsStreamAsync(abort.Token));
            await reader.ReadUntilAsync(static f => f.EventName == sentinel, Bound);
            frames = [.. reader.Frames.Where(static f => !f.IsHeartbeat && f.EventName != sentinel)];
        }

        await abort.CancelAsync();

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            response?.StatusCode.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue("the admitted stream subscribes to the bus");
            frames.Should().ContainSingle("the event is one frame").Which.Should().Match<SseFrame>(
                f => f.EventLines.Count == 1 && f.DataLines.Count == 1 && f.Lines.Count == 2,
                "the frame has exactly one event: line and exactly one data: line");
            frames.Select(static f => f.EventName).Should().Equal(["queue.a%0D%0Adata: x.updated"], "CR and LF in the event: value are written percent-encoded");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldDeliverWholeFramesInOrderAndKeepHeartbeating_WhenReaderIsSlow(bool allowSynchronousIO)
    {
        const int n = 1000;
        var pad = new string('x', 20_000); // ≈20 MB in all, as C7.md's slow-reader run
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            BusCapacity = 8192, // ≥ n, so a frame the bus dropped is never read as one the stream lost
            HeartbeatInterval = TimeSpan.FromMilliseconds(10),
            PerConnectionBoundBytes = 64L * 1024 * 1024, // ≥ everything published: tests serialization, not Q1's policy
        });

        using var abort = new CancellationTokenSource();
        var responseTask = host.OpenStreamAsync(string.Empty, abort.Token);
        var headersArrived = await Task.WhenAny(responseTask, Task.Delay(Bound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
        using var response = headersArrived ? await responseTask : null;
        var subscribed = response?.StatusCode == HttpStatusCode.OK
            && await host.Bus.Subscribers.WaitUntilAsync(static c => c >= 1, Bound);

        var readTask = Task.FromResult<(IReadOnlyList<SseFrame> Frames, bool SawLast, bool HeartbeatAfter)>(([], false, false));
        if (subscribed)
        {
            var body = await response!.Content.ReadAsStreamAsync(abort.Token);
            readTask = Task.Run(async () =>
            {
                using var reader = new SseReader(new ThrottledReadStream(body, 16 * 1024, TimeSpan.FromMilliseconds(2)));
                var sawLast = await reader.ReadUntilAsync(static f => LoadFrames.Sequence(f) == n - 1, TimeSpan.FromSeconds(60));
                var lastEvent = reader.Frames.Count;
                var heartbeatAfter = sawLast && await reader.ReadUntilAsync(static f => f.IsHeartbeat, TimeSpan.FromSeconds(5));
                return ((IReadOnlyList<SseFrame>)[.. reader.Frames], sawLast, heartbeatAfter && reader.Frames.Count > lastEvent);
            });
        }

        for (var i = 0; i < n; i++)
        {
            await host.PublishAsync(LoadFrames.Topic(i), correlationId: LoadFrames.Correlation(i, pad));
            if (i % 10 == 9)
                await Task.Delay(20); // fence-allow: SIMULATED-WORK — publishes in bursts of ten so heartbeats fall due between them (C7.md)
        }

        var (frames, sawLast, heartbeatAfter) = await readTask;
        await abort.CancelAsync();

        var events = frames.Where(static f => !f.IsHeartbeat).ToList();
        var sequence = events.Select(LoadFrames.Sequence).ToList();
        var malformed = events.Where(f => !LoadFrames.IsWellFormed(f, pad)).Select(static f => f.ToString()).Take(3).ToList();

        // Heartbeats throughout the burst: every tenth of the events has at least one heartbeat among its frames.
        var heartbeatsPerTenth = new int[10];
        var seen = 0;
        foreach (var frame in frames)
        {
            if (frame.IsHeartbeat && seen > 0 && seen < n)
                heartbeatsPerTenth[Math.Min(9, seen * 10 / n)]++;
            else if (!frame.IsHeartbeat)
                seen++;
        }

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            response?.StatusCode.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue("the admitted stream subscribes to the bus");
            sawLast.Should().BeTrue("the slow reader receives the last event");
            malformed.Should().BeEmpty("every frame is exactly ': heartbeat' or one whole event frame");
            sequence.Should().Equal(Enumerable.Range(0, n), "events 0..999 arrive in order, none missing or repeated");
            heartbeatsPerTenth.Should().OnlyContain(static c => c > 0, "heartbeats keep arriving throughout the burst (per tenth of the events: {0})", string.Join(",", heartbeatsPerTenth));
            heartbeatAfter.Should().BeTrue("heartbeats keep arriving after the burst");
            host.BusDrops.Dropped.Should().Be(0, "the bus dropped nothing");
            host.LoggedExceptions.Select(static l => l.Exception!.GetType().Name + ": " + l.Exception.Message)
                .Should().BeEmpty("the host logs no exception");
        }
    }
}

/// <summary>
/// Spec <c>push-sse-stream-delivery</c>, requirement <i>One slow connection never stalls the bus or grows
/// without limit</i>, policy-agnostic half: a stopped reader does not stall another subscriber. The
/// queue-depth half is written in task 2.2 against its internal hook (it does not exist on unfixed code).
/// </summary>
public sealed class SseStoppedReaderTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    public static TheoryData<bool> Settings() => new() { false, true };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldNotStallAnotherSubscriber_WhenItsReaderStopsReading(bool allowSynchronousIO)
    {
        const int m = 2000;
        var pad = new string('x', 16_000); // ≈32 MB: far past the bound and the transport's buffers
        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            BusCapacity = 4096, // ≥ m (design D7)
        });

        var other = new CountingObserver();
        using var otherSubscription = host.Bus.AsObservable().Subscribe(other);

        using var abort = new CancellationTokenSource();
        var responseTask = host.OpenStreamAsync(string.Empty, abort.Token);
        var headersArrived = await Task.WhenAny(responseTask, Task.Delay(Bound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
        using var response = headersArrived ? await responseTask : null;

        // The SSE client reads nothing after the headers.
        var subscribed = response?.StatusCode == HttpStatusCode.OK
            && await host.Bus.Subscribers.WaitUntilAsync(static c => c >= 2, Bound);

        for (var i = 0; i < m; i++)
            await host.PublishAsync(LoadFrames.Topic(i), correlationId: LoadFrames.Correlation(i, pad));

        var allReceived = await other.Received.WaitUntilAsync(static c => c >= m, Bound);
        var received = other.Received.Value;
        var dropped = host.BusDrops.Dropped;

        // Unblock a stream the unfixed code left writing synchronously, so the host can stop.
        await abort.CancelAsync();
        response?.Dispose();

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            response?.StatusCode.Should().Be(HttpStatusCode.OK);
            subscribed.Should().BeTrue("the stopped reader's stream is admitted and subscribed next to the other subscriber");
            allReceived.Should().BeTrue("the other subscriber receives every event within the bound (it received {0} of {1})", received, m);
            dropped.Should().Be(0, "the bus dropped nothing, so a missing event is a stall, not a bus drop");
        }
    }
}

/// <summary>
/// Spec <c>push-sse-stream-delivery</c>, requirement <i>A disconnect ends the stream cleanly</i>. Runs alone:
/// it asserts on <c>TaskScheduler.UnobservedTaskException</c>, which is process-wide.
/// </summary>
[Collection(SseProcessWideStateGroup.Name)]
public sealed class SseAbortTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    public static TheoryData<bool> Settings() => new() { false, true };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldCompleteAndUnsubscribeWithoutError_WhenClientAbortsMidBurst(bool allowSynchronousIO)
    {
        const int m = 2000;
        var pad = new string('x', 16_000);
        FlushFinalizers();
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.Enqueue(e.Exception);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await using var host = await SseTestHost.StartAsync(new SseHostOptions
            {
                AllowSynchronousIO = allowSynchronousIO,
                BusCapacity = 4096,
            });

            using var abort = new CancellationTokenSource();
            var responseTask = host.OpenStreamAsync(string.Empty, abort.Token);
            var headersArrived = await Task.WhenAny(responseTask, Task.Delay(Bound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
            var response = headersArrived ? await responseTask : null;
            var status = response?.StatusCode;
            var subscribed = status == HttpStatusCode.OK
                && await host.Bus.Subscribers.WaitUntilAsync(static c => c >= 1, Bound);

            var publishing = Task.Run(async () =>
            {
                for (var i = 0; i < m; i++)
                    await host.PublishAsync(LoadFrames.Topic(i), correlationId: LoadFrames.Correlation(i, pad));
            });

            // Read a few frames of the burst, then abort while frames are still being written.
            var readSome = false;
            if (subscribed)
            {
                using var reader = new SseReader(await response!.Content.ReadAsStreamAsync(abort.Token));
                readSome = await reader.ReadUntilAsync(static f => LoadFrames.Sequence(f) >= 10, Bound);
            }

            await abort.CancelAsync();
            response?.Dispose();
            await publishing;

            var completed = await host.StreamRequestsCompleted.WaitUntilAsync(static c => c >= 1, Bound);
            var unsubscribed = await host.Bus.Subscribers.WaitUntilAsync(static c => c == 0, Bound);
            var errors = host.Logs.Snapshot()
                .Where(static l => l.Level >= LogLevel.Error)
                .Select(static l => $"{l.Level} {l.Category}: {l.Message} {l.Exception?.GetType().Name}")
                .Distinct()
                .ToList();

            FlushFinalizers();

            using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
            {
                status.Should().Be(HttpStatusCode.OK);
                readSome.Should().BeTrue("the client reads part of the burst before it aborts");
                completed.Should().BeTrue("the aborted request completes on the server");
                unsubscribed.Should().BeTrue("the bus keeps no subscriber for the aborted connection (it has {0})", host.Bus.Subscribers.Value);
                errors.Should().BeEmpty("the host logs no error for the abort");
                unobserved.Select(static e => e.GetType().Name + ": " + e.Message).Should().BeEmpty("no task exception goes unobserved");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static void FlushFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
