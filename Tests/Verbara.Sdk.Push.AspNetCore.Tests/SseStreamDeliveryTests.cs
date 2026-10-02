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

/// <summary>
/// Spec <c>push-sse-stream-delivery</c>, the policy Q1 ruled (design <i>Owner answers</i>, Q1 2026-10-01):
/// drop oldest at a byte bound (1 MiB per connection by default), then one <c>event: .gap</c> carrying the
/// count of dropped event frames; heartbeats outside the bound; a metric and one Warning per gap episode;
/// <c>.gap</c> reserved. Runs alone: the drop metric is read by meter name, process-wide. The queue-depth
/// assertion, the public option's non-default value and a single frame larger than the bound are task 2.2's.
/// </summary>
[Collection(SseProcessWideStateGroup.Name)]
public sealed class SseStreamBoundTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    /// <summary>The bus's own instruments; the stream's drop metric is any other counter on the push meter.</summary>
    private static readonly HashSet<string> BusInstruments = new(StringComparer.Ordinal)
    {
        "asterisk.push.events.published",
        "asterisk.push.events.delivered",
        "asterisk.push.events.dropped",
        "asterisk.push.subscribers.active",
    };

    /// <summary>What a stopped-then-resumed reader saw, and what the host recorded.</summary>
    private sealed record Episode(
        HttpStatusCode? Status,
        bool Subscribed,
        bool BusDelivered,
        int BusReceived,
        bool SawLast,
        IReadOnlyList<SseFrame> Frames,
        long BusDropped,
        IReadOnlyDictionary<string, long> StreamMetric,
        IReadOnlyList<CapturedLog> Logs,
        string Pad,
        int Published);

    public static TheoryData<int, bool> PayloadsAndSettings() => new()
    {
        { 16_000, false },
        { 16_000, true },
        { 500, false },
        { 500, true },
    };

    public static TheoryData<bool> Settings() => new() { false, true };

    [Theory]
    [MemberData(nameof(PayloadsAndSettings))]
    public async Task Stream_ShouldSendOneGapWithTheDroppedEventCount_WhenStoppedReaderResumesAfterTheBound(int payload, bool allowSynchronousIO)
    {
        var episode = await RunStoppedReaderEpisodeAsync(payload, allowSynchronousIO, heartbeat: null);
        var gaps = GapAnalysis.Of(episode.Frames, episode.Published);

        using (new AssertionScope($"payload={payload} (AllowSynchronousIO={allowSynchronousIO})"))
        {
            AssertEpisodeRan(episode);
            gaps.Gaps.Should().NotBeEmpty("events were dropped at the bound, and the client is told");
            gaps.BackToBackGaps.Should().Be(0, "a slow client gets exactly one .gap before the next event frame, never two in a row");
            gaps.Gaps.Select(static g => g.Count).Should().Equal(gaps.Gaps.Select(static g => (long?)g.DroppedBetweenNeighbours),
                "each .gap carries the number of event frames dropped since the previous frame, heartbeats excluded, so every frame after it is newer than every dropped one");
            gaps.Gaps.Sum(static g => g.Count ?? 0).Should().Be(gaps.Missing, "every event that never arrived is reported by a .gap ({0} of {1} never arrived)", gaps.Missing, episode.Published);
            gaps.EventSequence.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("events still arrive in publish order, none repeated");
            gaps.EventBytesAfterLastGap.Should().BeLessThanOrEqualTo(SseStreamSettings.DefaultBoundBytes,
                "the bytes queued for the connection (what the last .gap is followed by) never exceed the 1 MiB default");
            gaps.EventBytesAfterLastGap.Should().BeGreaterThan(SseStreamSettings.DefaultBoundBytes - (2 * gaps.LargestEventFrameBytes),
                "the default bound is 1 MiB of UTF-8 frames, not less (one frame is {0} bytes)", gaps.LargestEventFrameBytes);
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldKeepHeartbeatsOutOfTheBound_WhenTheQueueIsFull(bool allowSynchronousIO)
    {
        var episode = await RunStoppedReaderEpisodeAsync(500, allowSynchronousIO, heartbeat: TimeSpan.FromMilliseconds(10));
        var gaps = GapAnalysis.Of(episode.Frames, episode.Published);

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            AssertEpisodeRan(episode);
            gaps.Gaps.Should().NotBeEmpty("events were dropped at the bound");
            gaps.Gaps.Sum(static g => g.Count ?? 0).Should().Be(gaps.Missing, "dropped heartbeats are not counted in a .gap");
            gaps.HeartbeatsBetweenLastGapAndNewestEvent.Should().Be(0,
                "a heartbeat that falls due while the queue is at the bound (a drop not yet reported) is not queued");
            gaps.HeartbeatsAfterNewestEvent.Should().BePositive("a connection below the bound still gets its heartbeats");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldCountDropsOnAMetricAndWarnOncePerEpisode_WhenTheBoundDropsFrames(bool allowSynchronousIO)
    {
        var episode = await RunStoppedReaderEpisodeAsync(16_000, allowSynchronousIO, heartbeat: null);
        var gaps = GapAnalysis.Of(episode.Frames, episode.Published);
        var reported = gaps.Gaps.Sum(static g => g.Count ?? 0);
        var warnings = episode.Logs
            .Where(static l => l.Level == LogLevel.Warning && !l.Category.StartsWith("Microsoft.", StringComparison.Ordinal))
            .Select(static l => $"{l.Category}: {l.Message}")
            .ToList();

        using (new AssertionScope($"AllowSynchronousIO={allowSynchronousIO}"))
        {
            AssertEpisodeRan(episode);
            reported.Should().BePositive("the episode dropped event frames");
            episode.StreamMetric.Values.Sum().Should().Be(reported,
                "a push metric counts the dropped event frames, as many as the .gap frames report (instruments seen: [{0}])", string.Join(",", episode.StreamMetric.Keys));
            warnings.Should().HaveCount(gaps.Gaps.Count, "one Warning is logged per gap episode ({0} .gap frames), never per frame", gaps.Gaps.Count);
            warnings.Should().NotBeEmpty("an episode that dropped frames is logged");
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Stream_ShouldNeverWriteAnEventAsTheGapMarker_WhenItsTypeIsTheReservedName(bool allowSynchronousIO)
    {
        const string sentinel = "queue.9.sentinel";
        await using var host = await SseTestHost.StartAsync(new SseHostOptions { AllowSynchronousIO = allowSynchronousIO });

        using var abort = new CancellationTokenSource();
        var responseTask = host.OpenStreamAsync(string.Empty, abort.Token);
        var headersArrived = await Task.WhenAny(responseTask, Task.Delay(Bound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
        using var response = headersArrived ? await responseTask : null;
        var subscribed = response?.StatusCode == HttpStatusCode.OK
            && await host.Bus.Subscribers.WaitUntilAsync(static c => c >= 1, Bound);

        // No topic path: today's frame names it by its EventType, ".gap".
        await host.Bus.PublishAsync(new ReservedNameEvent { Metadata = new PushEventMetadata("T1", null, DateTimeOffset.UtcNow, null) });
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
            frames.Should().ContainSingle("the event is still delivered, under another name");
            frames.Select(static f => f.EventName).Should().NotContain(".gap", "only the stream's own gap marker uses that name");
        }
    }

    private static void AssertEpisodeRan(Episode episode)
    {
        episode.Status.Should().Be(HttpStatusCode.OK);
        episode.Subscribed.Should().BeTrue("the stopped reader's stream is admitted and subscribed");
        episode.BusDelivered.Should().BeTrue("the bus hands every event on while the reader is stopped (it delivered {0} of {1})", episode.BusReceived, episode.Published);
        episode.BusDropped.Should().Be(0, "the bus dropped nothing, so every missing event was dropped by the connection's bound");
        episode.SawLast.Should().BeTrue("the resumed reader receives the newest event");
        episode.Frames.Where(f => !f.IsHeartbeat && f.EventName != ".gap" && !LoadFrames.IsWellFormed(f, episode.Pad))
            .Select(static f => f.ToString()).Should().BeEmpty("every frame is a heartbeat, the gap marker or one whole event frame");
    }

    /// <summary>One <c>.gap</c> frame: what it reports and what its neighbouring event frames say was dropped.</summary>
    private sealed record Gap(long? Count, long DroppedBetweenNeighbours);

    /// <summary>The <c>.gap</c> frames of a resumed stream, read against the sequence numbers around them.</summary>
    private sealed record GapAnalysis(
        IReadOnlyList<Gap> Gaps,
        int BackToBackGaps,
        IReadOnlyList<int> EventSequence,
        int Missing,
        long EventBytesAfterLastGap,
        long LargestEventFrameBytes,
        int HeartbeatsBetweenLastGapAndNewestEvent,
        int HeartbeatsAfterNewestEvent)
    {
        public static GapAnalysis Of(IReadOnlyList<SseFrame> frames, int published)
        {
            var gaps = new List<Gap>();
            var backToBack = 0;
            var lastGapIndex = -1;
            var newestIndex = -1;
            var previousSequence = -1;
            long? pending = null;
            var previousWasGap = false;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame.EventName == ".gap")
                {
                    if (previousWasGap)
                        backToBack++;
                    pending = GapCount(frame);
                    previousWasGap = true;
                    lastGapIndex = i;
                    continue;
                }

                var sequence = LoadFrames.Sequence(frame);
                if (sequence < 0)
                    continue;

                if (previousWasGap)
                    gaps.Add(new Gap(pending, sequence - previousSequence - 1));
                previousWasGap = false;
                previousSequence = sequence;
                if (sequence == published - 1)
                    newestIndex = i;
            }

            if (previousWasGap)
                gaps.Add(new Gap(pending, published - previousSequence - 1));

            var sequenceNumbers = frames.Select(LoadFrames.Sequence).Where(static s => s >= 0).ToList();
            var afterLastGap = lastGapIndex < 0 ? [] : frames.Skip(lastGapIndex + 1).Where(static f => LoadFrames.Sequence(f) >= 0).ToList();
            return new GapAnalysis(
                gaps,
                backToBack,
                sequenceNumbers,
                published - sequenceNumbers.Distinct().Count(),
                afterLastGap.Sum(static f => (long)f.Utf8Bytes),
                afterLastGap.Count == 0 ? 0 : afterLastGap.Max(static f => (long)f.Utf8Bytes),
                lastGapIndex >= 0 && newestIndex > lastGapIndex ? frames.Skip(lastGapIndex + 1).Take(newestIndex - lastGapIndex).Count(static f => f.IsHeartbeat) : -1,
                newestIndex < 0 ? 0 : frames.Skip(newestIndex + 1).Count(static f => f.IsHeartbeat));
        }
    }

    /// <summary>The count a gap frame carries: a bare integer, or a JSON object with a <c>dropped</c> number.</summary>
    private static long? GapCount(SseFrame? gap)
    {
        if (gap?.Data is not { } data)
            return null;
        if (long.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
            return bare;
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("dropped", out var dropped) && dropped.TryGetInt64(out var n) ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A client that stops reading after the headers while events are published far past the 1 MiB bound
    /// and the transport's buffers (≈32 MB at 16 KB, ≈20 MB at 0.5 KB), then reads everything.
    /// </summary>
    private static async Task<Episode> RunStoppedReaderEpisodeAsync(int payload, bool allowSynchronousIO, TimeSpan? heartbeat)
    {
        var count = payload >= 16_000 ? 2_000 : 40_000;
        var pad = new string('x', payload);

        var streamMetric = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith(PushMetrics.MeterName, StringComparison.Ordinal) && !BusInstruments.Contains(instrument.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) => streamMetric.AddOrUpdate(instrument.Name, value, (_, v) => v + value));
        meterListener.SetMeasurementEventCallback<int>((instrument, value, _, _) => streamMetric.AddOrUpdate(instrument.Name, value, (_, v) => v + value));
        meterListener.Start();

        await using var host = await SseTestHost.StartAsync(new SseHostOptions
        {
            AllowSynchronousIO = allowSynchronousIO,
            BusCapacity = count + 16, // ≥ the events published (design D7)
            HeartbeatInterval = heartbeat,
        });

        var probe = new CountingObserver();
        using var probeSubscription = host.Bus.AsObservable().Subscribe(probe);

        using var abort = new CancellationTokenSource();
        var responseTask = host.OpenStreamAsync(string.Empty, abort.Token);
        var headersArrived = await Task.WhenAny(responseTask, Task.Delay(Bound)) == responseTask; // fence-allow: GUARD-TIMEOUT — failure bound on the response headers, never the winning arm of a green run
        using var response = headersArrived ? await responseTask : null;
        var status = response?.StatusCode;
        var subscribed = status == HttpStatusCode.OK
            && await host.Bus.Subscribers.WaitUntilAsync(static c => c >= 2, Bound);

        // The reader is stopped: nothing reads the body while the events are published.
        for (var i = 0; i < count; i++)
            await host.PublishAsync(LoadFrames.Topic(i), correlationId: LoadFrames.Correlation(i, pad));

        // A last event the stream never receives (another tenant): once the probe has it, the bus has
        // finished handing the stream every load event, so the queue holds its final content.
        await host.PublishAsync("queue.0.flush", tenant: "T2");
        var busDelivered = await probe.Received.WaitUntilAsync(c => c >= count + 1, Bound);
        var busReceived = probe.Received.Value;

        // The reader resumes and reads until the newest event.
        IReadOnlyList<SseFrame> frames = [];
        var sawLast = false;
        if (subscribed && busDelivered)
        {
            using var reader = new SseReader(await response!.Content.ReadAsStreamAsync(abort.Token));
            sawLast = await reader.ReadUntilAsync(f => LoadFrames.Sequence(f) == count - 1, TimeSpan.FromSeconds(60));
            if (sawLast && heartbeat is not null)
                await reader.ReadUntilAsync(static f => f.IsHeartbeat, Bound);
            frames = [.. reader.Frames];
        }

        var logs = host.Logs.Snapshot();
        var busDropped = host.BusDrops.Dropped;

        // Unblock a stream the unfixed code left writing synchronously, so the host can stop.
        await abort.CancelAsync();
        response?.Dispose();

        return new Episode(status, subscribed, busDelivered, busReceived, sawLast, frames, busDropped, new Dictionary<string, long>(streamMetric, StringComparer.Ordinal), logs, pad, count);
    }
}
