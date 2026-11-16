using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// What <see cref="IAudioServer.OnStreamConnected"/> promises about a blocking and a throwing observer, measured
/// through <see cref="CompositeAudioServer"/> — the <see cref="IAudioServer"/> AddVerbara registers — over a real
/// <see cref="AudioSocketServer"/> and a real <see cref="WebSocketAudioServer"/> sharing one options instance, with
/// the same scenario subscribed on each server directly as the control.
/// </summary>
/// <remarks>
/// The composite's <c>OnStreamConnected</c> is an Rx <c>Merge</c> of the two servers' subjects. Each subscription
/// to it is one merge sink whose inner observers forward under one gate per sink, and the consumer's observer is
/// wrapped in a safe observer that disposes the subscription when its <c>OnNext</c> throws.
/// </remarks>
public sealed class CompositeAudioServerObserverTests(ITestOutputHelper output)
{
    public enum Transport
    {
        AudioSocket,
        WebSocket,
    }

    public enum Route
    {
        /// <summary>Subscribed through <see cref="CompositeAudioServer.OnStreamConnected"/>.</summary>
        Composite,

        /// <summary>Subscribed on each server's own <c>OnStreamConnected</c>: the control.</summary>
        Direct,
    }

    /// <summary>Bound on every wait for something that should happen.</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>How long an announcement that should already have happened is given before it counts as held.</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(2);

    /// <summary>Bound on the blocking observer, so a test that never opens the gate cannot hang the lane.</summary>
    private static readonly TimeSpan GateLimit = TimeSpan.FromSeconds(30);

    private const int AudioPayloadLength = 320;

    /// <summary>
    /// Measured 2026-11-16 on e0506d98, 5 of 5 runs each: through the composite a second connection on either
    /// server is accepted, registered, counted and carries audio while an earlier observer blocks, but is not
    /// announced to a later observer until the blocked one returns (the merge forwards under one gate per
    /// subscription); and an observer that throws is unsubscribed from the composite for good (Rx's safe observer
    /// disposes the subscription), while a direct subscriber that threw keeps receiving later streams.
    /// </summary>
    private const string H250Skip = "H250: measured red, awaiting the owner's wording decision";

    // ---- P1 / P2: an observer that blocks ------------------------------------------------------------------------

    [Theory(Skip = H250Skip)]
    [InlineData(Transport.AudioSocket, Transport.AudioSocket)]
    [InlineData(Transport.AudioSocket, Transport.WebSocket)]
    [InlineData(Transport.WebSocket, Transport.WebSocket)]
    [InlineData(Transport.WebSocket, Transport.AudioSocket)]
    public async Task OnStreamConnected_ShouldServeAndAnnounceAnotherConnection_WhenAnEarlierCompositeObserverBlocks(
        Transport first, Transport second)
    {
        var outcome = await BlockingScenarioAsync(Route.Composite, first, second);

        using var scope = new AssertionScope();
        outcome.Accepted.Should().BeTrue("other connections are accepted while an observer runs");
        outcome.Registered.Should().BeTrue("and served: registered");
        outcome.Found.Should().BeTrue("and found");
        outcome.CountWhileBlocked.Should().Be(2, "and counted");
        outcome.AnnouncedWhileBlocked.Should().BeTrue("and announced to the observers that are not blocked");
        outcome.AudioFlowed.Should().BeTrue("and their audio flows");
        outcome.AnnouncedAfterRelease.Should().BeTrue("in every case it is announced once the observer returns");
        outcome.GateTimedOut.Should().BeFalse("the test opened the gate within its bound");
    }

    [Theory]
    [InlineData(Transport.AudioSocket, Transport.AudioSocket)]
    [InlineData(Transport.AudioSocket, Transport.WebSocket)]
    [InlineData(Transport.WebSocket, Transport.WebSocket)]
    [InlineData(Transport.WebSocket, Transport.AudioSocket)]
    public async Task OnStreamConnected_ShouldServeAndAnnounceAnotherConnection_WhenAnEarlierObserverOnTheServerBlocks(
        Transport first, Transport second)
    {
        var outcome = await BlockingScenarioAsync(Route.Direct, first, second);

        using var scope = new AssertionScope();
        outcome.Accepted.Should().BeTrue("other connections are accepted while an observer runs");
        outcome.Registered.Should().BeTrue("and served: registered");
        outcome.Found.Should().BeTrue("and found");
        outcome.CountWhileBlocked.Should().Be(2, "and counted");
        outcome.AnnouncedWhileBlocked.Should().BeTrue("and announced to the observers that are not blocked");
        outcome.AudioFlowed.Should().BeTrue("and their audio flows");
        outcome.AnnouncedAfterRelease.Should().BeTrue("in every case it is announced once the observer returns");
        outcome.GateTimedOut.Should().BeFalse("the test opened the gate within its bound");
    }

    [Theory]
    [InlineData(Route.Composite, Transport.AudioSocket)]
    [InlineData(Route.Composite, Transport.WebSocket)]
    [InlineData(Route.Direct, Transport.AudioSocket)]
    [InlineData(Route.Direct, Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldHoldTheConnectionsRegistration_WhileAnObserverBlocksAfterTheFarEndHungUp(
        Route route, Transport first)
    {
        // P2: the blocked connection's far end hangs up; its registration and its place stay until the observer returns.
        await using var rig = await Rig.StartAsync();
        using var gate = new ManualResetEventSlim(false);
        var blocked = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateTimedOut = 0;
        using var blocker = rig.Subscribe(route, stream =>
        {
            if (!blocked.TrySetResult(stream))
                return;
            if (!gate.Wait(GateLimit))
                Interlocked.Exchange(ref gateTimedOut, 1);
        });

        try
        {
            using var call = await Call.OpenAsync(rig, first);
            var stream = await blocked.Task.WaitAsync(WaitLimit);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var states = stream.StateChanges.Subscribe(state =>
            {
                if (state is AudioStreamState.Disconnected or AudioStreamState.Error)
                    disconnected.TrySetResult();
            });

            call.Dispose();
            await disconnected.Task.WaitAsync(WaitLimit);

            var heldStream = rig.Composite.GetStream(call.Key);
            var heldCount = rig.Composite.ActiveStreamCount;
            output.WriteLine($"P2 route={route} first={first}: after hang-up GetStream={(heldStream is null ? "null" : "held")} ActiveStreamCount={heldCount}");

            using (new AssertionScope())
            {
                heldStream.Should().BeSameAs(stream, "a blocking observer holds that connection's registration until it returns");
                heldCount.Should().Be(1, "and its place");
            }
        }
        finally
        {
            gate.Set();
        }

        Volatile.Read(ref gateTimedOut).Should().Be(0, "the test opened the gate within its bound");
    }

    // ---- P3: an observer that throws -----------------------------------------------------------------------------

    [Theory]
    [InlineData(Route.Composite, Transport.AudioSocket)]
    [InlineData(Route.Composite, Transport.WebSocket)]
    [InlineData(Route.Direct, Transport.AudioSocket)]
    [InlineData(Route.Direct, Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldCloseAndLogOnlyThatConnectionAndKeepAnnouncing_WhenAnObserverThrows(
        Route route, Transport first)
    {
        var outcome = await ThrowingScenarioAsync(route, first);

        using var scope = new AssertionScope();
        outcome.ClosedByServer.Should().BeTrue("an observer that throws loses that connection: the server closes it");
        outcome.RegisteredAfterClose.Should().BeFalse("and releases it");
        outcome.CountAfterClose.Should().Be(0, "and gives its place back");
        outcome.Logged.Should().BeTrue("the server logs the failure");
        outcome.EarlierGotFirst.Should().BeTrue("an observer subscribed before the throwing one receives the stream");
        outcome.LaterGotFirst.Should().BeFalse("observers subscribed after the throwing one do not receive that stream");
        outcome.EarlierGotLater.Should().Be(2, "a subscriber from before the throw is still announced later streams of both servers");
        outcome.LaterGotLater.Should().Be(2, "so is one subscribed after the throwing one");
        outcome.FreshGotLater.Should().Be(2, "and a subscription made after the throw");
    }

    [Theory(Skip = H250Skip)]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldKeepAnnouncingLaterStreamsToTheObserverThatThrew_WhenSubscribedThroughTheComposite(
        Transport first)
    {
        // Not in the published text, which says only that the throwing observer loses that connection: this is the
        // parity with a direct subscription, measured in the Direct theory below.
        var outcome = await ThrowingScenarioAsync(Route.Composite, first);

        outcome.ThrowerGotLater.Should().Be(2, "a direct subscriber that threw is still announced later streams of both servers");
    }

    [Theory]
    [InlineData(Transport.AudioSocket)]
    [InlineData(Transport.WebSocket)]
    public async Task OnStreamConnected_ShouldKeepAnnouncingLaterStreamsToTheObserverThatThrew_WhenSubscribedOnTheServer(
        Transport first)
    {
        var outcome = await ThrowingScenarioAsync(Route.Direct, first);

        outcome.ThrowerGotLater.Should().Be(2, "a subject keeps an observer whose OnNext threw");
    }

    // ---- Scenarios ----------------------------------------------------------------------------------------------

    private sealed record BlockingOutcome(
        Route Route,
        Transport First,
        Transport Second,
        bool Accepted,
        double HandshakeMs,
        bool Registered,
        bool Found,
        int CountWhileBlocked,
        bool AnnouncedWhileBlocked,
        bool AudioFlowed,
        bool AnnouncedAfterRelease,
        double AnnouncedAfterMs,
        bool GateTimedOut);

    private async Task<BlockingOutcome> BlockingScenarioAsync(Route route, Transport first, Transport second)
    {
        await using var rig = await Rig.StartAsync();
        using var gate = new ManualResetEventSlim(false);
        var blockedOn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateTimedOut = 0;
        var announced = new Announcements();

        // The blocking observer subscribes first, so the non-blocking one is the "later" observer of each stream.
        using var blocker = rig.Subscribe(route, stream =>
        {
            if (!blockedOn.TrySetResult(stream.ChannelId))
                return;
            if (!gate.Wait(GateLimit))
                Interlocked.Exchange(ref gateTimedOut, 1);
        });
        using var watcher = rig.Subscribe(route, announced.Add);

        try
        {
            using var callA = await Call.OpenAsync(rig, first);
            (await blockedOn.Task.WaitAsync(WaitLimit)).Should().Be(callA.Key, "the first stream reaches the blocking observer");
            (await rig.Registrations.WaitAsync(WaitLimit)).Should().BeTrue("A was registered before it was announced");

            var clock = Stopwatch.StartNew();
            bool accepted;
            Call? opened = null;
            try
            {
                opened = await Call.OpenAsync(rig, second).WaitAsync(WaitLimit);
                accepted = true;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or InvalidOperationException or IOException)
            {
                accepted = false;
            }

            var handshakeMs = clock.Elapsed.TotalMilliseconds;
            using var callB = opened;
            var registered = accepted && await rig.Registrations.WaitAsync(WaitLimit);
            var found = callB is not null && rig.Composite.GetStream(callB.Key) is not null;
            var count = rig.Composite.ActiveStreamCount;
            var announcedWhileBlocked = callB is not null && await announced.WaitAsync(callB.Key, QuietWindow);
            var audioFlowed = callB is not null && await callB.AudioFlowsAsync(rig.Composite.GetStream(callB.Key));

            gate.Set();
            var released = Stopwatch.StartNew();
            var announcedAfter = callB is not null && await announced.WaitAsync(callB.Key, WaitLimit);
            var announcedAfterMs = released.Elapsed.TotalMilliseconds;

            var outcome = new BlockingOutcome(
                route, first, second, accepted, handshakeMs, registered, found, count, announcedWhileBlocked,
                audioFlowed, announcedAfter, announcedAfterMs, Volatile.Read(ref gateTimedOut) == 1);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"BLOCK route={route} A={first} B={second}: accepted={accepted} handshakeMs={handshakeMs:F1} registered={registered} found={found} count={count} announcedWhileBlocked={announcedWhileBlocked} audioFlowed={audioFlowed} announcedAfterRelease={announcedAfter} afterReleaseMs={announcedAfterMs:F1} gateTimedOut={outcome.GateTimedOut}"));
            return outcome;
        }
        finally
        {
            gate.Set();
        }
    }

    private sealed record ThrowOutcome(
        bool ClosedByServer,
        bool RegisteredAfterClose,
        int CountAfterClose,
        bool Logged,
        bool EarlierGotFirst,
        bool LaterGotFirst,
        int EarlierGotLater,
        int ThrowerGotLater,
        int LaterGotLater,
        int FreshGotLater);

    private async Task<ThrowOutcome> ThrowingScenarioAsync(Route route, Transport first)
    {
        await using var rig = await Rig.StartAsync();
        var earlier = new Announcements();
        var thrower = new Announcements();
        var later = new Announcements();
        var threw = 0;

        using var earlierSubscription = rig.Subscribe(route, earlier.Add);
        using var throwerSubscription = rig.Subscribe(route, stream =>
        {
            thrower.Add(stream);
            if (Interlocked.Exchange(ref threw, 1) == 0)
                throw new InvalidOperationException("H250 observer failure");
        });
        using var laterSubscription = rig.Subscribe(route, later.Add);

        using var callA = await Call.OpenAsync(rig, first);
        var earlierGotFirst = await earlier.WaitAsync(callA.Key, WaitLimit);
        var closed = await callA.ClosedByServerAsync(WaitLimit);
        var registeredAfterClose = rig.Composite.GetStream(callA.Key) is not null;
        var countAfterClose = rig.Composite.ActiveStreamCount;
        var logged = await rig.WhenConnectionErrorLogged(first, "H250 observer failure", WaitLimit);
        var laterGotFirst = later.Contains(callA.Key);

        var fresh = new Announcements();
        using var freshSubscription = rig.Subscribe(route, fresh.Add);

        int earlierGot = 0, throwerGot = 0, laterGot = 0, freshGot = 0;
        foreach (var transport in new[] { Transport.AudioSocket, Transport.WebSocket })
        {
            using var call = await Call.OpenAsync(rig, transport);

            // The subscribers are called in the order they subscribed, synchronously, so once the last one has the
            // stream every earlier one has had its call.
            var freshHas = await fresh.WaitAsync(call.Key, WaitLimit);
            freshGot += freshHas ? 1 : 0;
            earlierGot += earlier.Contains(call.Key) ? 1 : 0;
            throwerGot += thrower.Contains(call.Key) ? 1 : 0;
            laterGot += later.Contains(call.Key) ? 1 : 0;
        }

        var outcome = new ThrowOutcome(closed, registeredAfterClose, countAfterClose, logged, earlierGotFirst,
            laterGotFirst, earlierGot, throwerGot, laterGot, freshGot);
        output.WriteLine(
            $"THROW route={route} A={first}: closedByServer={closed} registeredAfterClose={registeredAfterClose} countAfterClose={countAfterClose} logged={logged} earlierGotA={earlierGotFirst} laterGotA={laterGotFirst} laterStreams(of 2): earlier={earlierGot} thrower={throwerGot} later={laterGot} fresh={freshGot}");
        return outcome;
    }

    // ---- Harness ------------------------------------------------------------------------------------------------

    /// <summary>The streams one observer was handed, by key, each awaitable.</summary>
    private sealed class Announcements
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _seen = new(StringComparer.Ordinal);

        public void Add(IAudioStream stream) => Slot(stream.ChannelId).TrySetResult();

        public bool Contains(string key) => _seen.TryGetValue(key, out var slot) && slot.Task.IsCompleted;

        public async Task<bool> WaitAsync(string key, TimeSpan limit)
        {
            try
            {
                await Slot(key).Task.WaitAsync(limit);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private TaskCompletionSource Slot(string key) =>
            _seen.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>
    /// Both servers on one options instance, composed in the order AddVerbara composes them, each with a capturing
    /// logger and a signal per registration.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(AudioServerOptions options)
        {
            AudioSocket = new AudioSocketServer(options, AudioSocketLog, TimeProvider.System);
            WebSocket = new WebSocketAudioServer(options, WebSocketLog, TimeProvider.System);
            AudioSocket.AfterRegistryAdd = () => Registrations.Release();
            WebSocket.AfterRegistryAdd = () => Registrations.Release();
            Composite = new CompositeAudioServer([AudioSocket, WebSocket]);
        }

        public CapturingLogger<AudioSocketServer> AudioSocketLog { get; } = new();

        public CapturingLogger<WebSocketAudioServer> WebSocketLog { get; } = new();

        public AudioSocketServer AudioSocket { get; }

        public WebSocketAudioServer WebSocket { get; }

        public CompositeAudioServer Composite { get; }

        /// <summary>Released once per connection either server registers.</summary>
        public SemaphoreSlim Registrations { get; } = new(0);

        public int AudioSocketPort { get; private set; }

        public int WebSocketPort { get; private set; }

        public static async Task<Rig> StartAsync()
        {
            // MediaStartTimeout zero: a WebSocket connection is announced under its path segment at once, without a
            // MEDIA_START, so the only wait between the upgrade and the announcement is the one under test.
            var (rig, audioSocketPort, webSocketPort) = await LoopbackServerBind.StartAsync(
                (audioSocketPort, webSocketPort) => new Rig(new AudioServerOptions
                {
                    ListenAddress = "127.0.0.1",
                    AudioSocketPort = audioSocketPort,
                    WebSocketPort = webSocketPort,
                    DefaultFormat = "slin16",
                    IdleTimeout = TimeSpan.FromSeconds(5),
                    MediaStartTimeout = TimeSpan.Zero,
                }),
                async r =>
                {
                    await r.AudioSocket.StartAsync();
                    await r.WebSocket.StartAsync();
                });
            rig.AudioSocketPort = audioSocketPort;
            rig.WebSocketPort = webSocketPort;
            return rig;
        }

        public IDisposable Subscribe(Route route, Action<IAudioStream> onNext) => route switch
        {
            Route.Composite => Composite.OnStreamConnected.Subscribe(onNext),
            _ => new CompositeDisposable(
                AudioSocket.OnStreamConnected.Subscribe(onNext),
                WebSocket.OnStreamConnected.Subscribe(onNext)),
        };

        public async Task<bool> WhenConnectionErrorLogged(Transport transport, string message, TimeSpan limit)
        {
            bool Match(CapturedLogEntry entry) =>
                entry.EventName == "ConnectionError" && entry.Exception?.Message == message;

            try
            {
                if (transport == Transport.AudioSocket)
                    await AudioSocketLog.WhenLogged(Match).WaitAsync(limit);
                else
                    await WebSocketLog.WhenLogged(Match).WaitAsync(limit);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await WebSocket.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                await AudioSocket.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                Registrations.Dispose();
            }
        }
    }

    /// <summary>One call's far end: an AudioSocket client that has identified itself, or an upgraded WebSocket peer.</summary>
    private sealed class Call : IDisposable
    {
        private readonly TcpClient? _audioSocket;
        private readonly RawWebSocketPeer? _webSocket;

        private Call(string key, TcpClient? audioSocket, RawWebSocketPeer? webSocket)
        {
            Key = key;
            _audioSocket = audioSocket;
            _webSocket = webSocket;
        }

        /// <summary>The key the server registers the stream under.</summary>
        public string Key { get; }

        /// <summary>Connects and identifies (AudioSocket) or completes the upgrade (WebSocket).</summary>
        public static async Task<Call> OpenAsync(Rig rig, Transport transport)
        {
            using var bound = new CancellationTokenSource(WaitLimit);
            if (transport == Transport.AudioSocket)
            {
                var uuid = Guid.NewGuid();
                var client = new TcpClient { NoDelay = true };
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, rig.AudioSocketPort, bound.Token);
                    await client.GetStream().WriteAsync(Frame(AudioFrameType.Uuid, uuid.ToByteArray(bigEndian: true)), bound.Token);
                    return new Call(uuid.ToString(), client, null);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            var key = "ch-" + Guid.NewGuid().ToString("N");
            var peer = await RawWebSocketPeer.ConnectAsync(rig.WebSocketPort, bound.Token);
            try
            {
                await peer.UpgradeAsync(key, bound.Token);
                return new Call(key, null, peer);
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }

        /// <summary>Sends one audio frame from the far end and reads it from the server's stream.</summary>
        public async Task<bool> AudioFlowsAsync(IAudioStream? stream)
        {
            if (stream is null)
                return false;

            var payload = new byte[AudioPayloadLength];
            payload.AsSpan().Fill(0x5a);
            if (_audioSocket is not null)
                await _audioSocket.GetStream().WriteAsync(Frame(AudioFrameType.Audio, payload));
            else
                await _webSocket!.SendBinaryAsync(payload);

            try
            {
                var frame = await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);
                return frame.Length == AudioPayloadLength;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        /// <summary>Whether the server ended the connection within the bound.</summary>
        public async Task<bool> ClosedByServerAsync(TimeSpan limit)
        {
            if (_webSocket is not null)
            {
                try
                {
                    var end = await _webSocket.Ended.WaitAsync(limit);
                    return end is RawConnectionEnd.Fin or RawConnectionEnd.Reset;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            using var bound = new CancellationTokenSource(limit);
            var buffer = new byte[256];
            try
            {
                while (true)
                {
                    var read = await _audioSocket!.GetStream().ReadAsync(buffer, bound.Token);
                    if (read == 0)
                        return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            _audioSocket?.Dispose();
            _webSocket?.Dispose();
        }

        private static byte[] Frame(AudioFrameType type, byte[] payload)
        {
            var frame = new byte[3 + payload.Length];
            frame[0] = (byte)type;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)payload.Length;
            payload.CopyTo(frame.AsSpan(3));
            return frame;
        }
    }
}
