using System.Reflection;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// What an ARI client built with <c>EventsEnabled = false</c> does and does not do: it opens no events connection on any
/// path, its REST resources work without one, <c>ConnectAsync</c> and <c>DisconnectAsync</c> touch nothing, and its state
/// reads <c>RestOnly</c> until it is disposed. The far end counts every TCP connection, so a dial cannot go unseen.
/// </summary>
/// <remarks>
/// Whether a real Asterisk lists an application for such a client is a functional test: here the far end is scripted.
/// </remarks>
public sealed class AriClientRestOnlyTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    // The lowest delay that is not a spin: 1 ms against this window is 50 reconnect intervals.
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan NegativeWindow = TimeSpan.FromMilliseconds(50);

    private const string Channel = """{"id":"ch-1","name":"PJSIP/100-1","state":"Down"}""";

    private static readonly FieldInfo PumpField =
        typeof(AriClient).GetField("_pump", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo CtsField =
        typeof(AriClient).GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo SocketField =
        typeof(AriClient).GetField("_webSocket", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // ── The option ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_ShouldOpenTheEventsConnectionAsBefore_WhenEventsEnabledIsNotSet()
    {
        await using var farEnd = new AriFarEnd();
        var options = new AriClientOptions { BaseUrl = farEnd.BaseUrl, Username = "u", Password = "p", Application = "app" };
        await using var sut = new AriClient(Options.Create(options), NullLogger<AriClient>.Instance);
        var received = new TaskCompletionSource<AriEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = sut.Subscribe(new FirstEvent(received));

        await sut.ConnectAsync();
        await farEnd.WhenUpgraded(1).WaitAsync(WaitLimit);
        await farEnd.SendAsync("Probe");
        var evt = await received.Task.WaitAsync(WaitLimit);

        using (new AssertionScope())
        {
            sut.State.Should().Be(AriConnectionState.Connected);
            sut.IsConnected.Should().BeTrue();
            evt.Type.Should().Be("Probe");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldStayRestOnly_WhenTheOptionsObjectChangesAfterConstruction()
    {
        await using var farEnd = new AriFarEnd();
        var options = RestOnly(farEnd.BaseUrl);
        await using var sut = new AriClient(Options.Create(options), NullLogger<AriClient>.Instance);

        options.EventsEnabled = true;
        await sut.ConnectAsync();

        using (new AssertionScope())
        {
            farEnd.Dials.Should().Be(0, "what the client is was read when it was built");
            sut.State.Should().Be(AriConnectionState.RestOnly);
        }
    }

    // ── No events connection ────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_ShouldDialNothing_WhenSeveralReconnectIntervalsPass()
    {
        await using var farEnd = new AriFarEnd();
        var options = RestOnly(farEnd.BaseUrl);
        options.AutoReconnect = true;
        options.ReconnectInitialDelay = ReconnectInterval;
        options.ReconnectMaxDelay = ReconnectInterval;
        await using var sut = new AriClient(Options.Create(options), NullLogger<AriClient>.Instance);

        await sut.ConnectAsync();
        await Task.Delay(NegativeWindow); // fence-allow: SETTLE — the claim is that no dial happens, so the delay is the window; a dial at the reconnect interval lands inside it (mutation control)

        using (new AssertionScope())
        {
            farEnd.Dials.Should().Be(0, "a client with its events off never dials");
            farEnd.Upgrades.Should().Be(0);
            sut.EventLoop.Should().BeNull("no events loop and so no reconnect loop ever started");
            PumpField.GetValue(sut).Should().BeNull("no event buffer exists");
            CtsField.GetValue(sut).Should().BeNull("no connection's token source exists");
            SocketField.GetValue(sut).Should().BeNull("no socket exists");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldWriteOneInformationLineAndNoConnectedLine_WhenCalled()
    {
        var logger = new CapturingLogger<AriClient>();
        await using var sut = new AriClient(Options.Create(RestOnly("http://127.0.0.1:1")), logger);

        await sut.ConnectAsync();

        using (new AssertionScope())
        {
            logger.Entries.Should().ContainSingle(e => e.EventName == "RestOnly").Which.Level.Should().Be(LogLevel.Information);
            logger.Entries.Should().NotContain(e => e.EventName == "Connected", "nothing connected");
            logger.Entries.Should().OnlyContain(e => !e.Message.Contains("p4ss", StringComparison.Ordinal), "no credential is logged");
        }
    }

    // ── REST without a connection ───────────────────────────────────────────

    [Fact]
    public async Task Channels_ShouldHoldAndUnholdAChannelOfAnotherClient_WhenTheClientNeverConnected()
    {
        await using var farEnd = new AriFarEnd { Respond = _ => (204, string.Empty) };
        var other = new AriClient(
            Options.Create(new AriClientOptions
            {
                BaseUrl = farEnd.BaseUrl, Username = "u", Password = "p", Application = "other", AutoReconnect = false,
            }),
            NullLogger<AriClient>.Instance);
        await using (other)
        {
            await other.ConnectAsync();
            await farEnd.WhenUpgraded(1).WaitAsync(WaitLimit);
            await using var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

            await sut.Channels.HoldAsync("ch-1");
            await sut.Channels.UnholdAsync("ch-1");

            using (new AssertionScope())
            {
                farEnd.Requests.Should().Equal(
                    "PUT /ari/channels/ch-1/hold HTTP/1.1", "DELETE /ari/channels/ch-1/hold HTTP/1.1");
                farEnd.Upgrades.Should().Be(1, "only the other client holds a connection");
                other.State.Should().Be(AriConnectionState.Connected, "the other client's connection is untouched");
                sut.State.Should().Be(AriConnectionState.RestOnly);
            }
        }
    }

    [Fact]
    public async Task Channels_ShouldSurfaceAsteriskConflict_WhenHoldingAChannelOutsideStasis()
    {
        await using var farEnd = new AriFarEnd
        {
            Respond = _ => (409, """{"message":"Channel not in Stasis application"}"""),
        };
        await using var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

        var hold = async () => await sut.Channels.HoldAsync("ch-1");

        await hold.Should().ThrowAsync<AriConflictException>("Asterisk's 409 surfaces unchanged");
        sut.State.Should().Be(AriConnectionState.RestOnly);
    }

    [Fact]
    public async Task Channels_ShouldReturnWhatAsteriskReported_WhenOriginatingIntoAnApplicationNobodyRegistered()
    {
        await using var farEnd = new AriFarEnd { Respond = _ => (200, Channel) };
        await using var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

        var channel = await sut.Channels.OriginateAsync("PJSIP/100");

        using (new AssertionScope())
        {
            channel.Id.Should().Be("ch-1", "the answer cannot tell whether anyone registered the application");
            sut.State.Should().Be(AriConnectionState.RestOnly);
            farEnd.Upgrades.Should().Be(0);
        }
    }

    // ── ConnectAsync and DisconnectAsync ────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_ShouldComplete_WhenNoListenerIsReachable()
    {
        await using var sut = new AriClient(Options.Create(RestOnly(UnreachableBaseUrl())), NullLogger<AriClient>.Instance);

        var connect = async () => await sut.ConnectAsync();

        await connect.Should().NotThrowAsync();
        sut.State.Should().Be(AriConnectionState.RestOnly);
    }

    [Fact]
    public async Task ConnectAsync_ShouldCompleteAndKeepTheState_WhenTheTokenIsAlreadyCancelled()
    {
        await using var farEnd = new AriFarEnd();
        await using var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

        var connect = async () => await sut.ConnectAsync(new CancellationToken(canceled: true));

        await connect.Should().NotThrowAsync("there is no dial for the token to bound");
        sut.State.Should().Be(AriConnectionState.RestOnly);
    }

    [Fact]
    public async Task ConnectAsync_ShouldChangeNothing_WhenCalledTwice()
    {
        await using var farEnd = new AriFarEnd();
        await using var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

        await sut.ConnectAsync();
        await sut.ConnectAsync();

        using (new AssertionScope())
        {
            sut.State.Should().Be(AriConnectionState.RestOnly);
            farEnd.Dials.Should().Be(0);
        }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldCompleteAndKeepTheState_WhenTheTokenIsAlreadyCancelled()
    {
        await using var farEnd = new AriFarEnd();
        await using var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

        var disconnect = async () => await sut.DisconnectAsync(new CancellationToken(canceled: true));

        await disconnect.Should().NotThrowAsync("there is no close to wait for");
        sut.State.Should().Be(AriConnectionState.RestOnly);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldLeaveRestUsable_WhenCalledAfterConnect()
    {
        await using var farEnd = new AriFarEnd { Respond = _ => (200, """{"ping":"pong","timestamp":"t","asterisk_id":"a"}""") };
        await using var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

        await sut.ConnectAsync();
        await sut.DisconnectAsync();
        await sut.Asterisk.PingAsync();

        using (new AssertionScope())
        {
            farEnd.Requests.Should().Equal(["GET /ari/asterisk/ping HTTP/1.1"], "the call reaches Asterisk");
            farEnd.Upgrades.Should().Be(0);
            sut.State.Should().Be(AriConnectionState.RestOnly);
        }
    }

    [Fact]
    public async Task CreateAndConnectAsync_ShouldReturnAClientAndDialNothing_WhenNoListenerIsReachable()
    {
        await using var farEnd = new AriFarEnd();
        var unreachable = RestOnly(UnreachableBaseUrl());
        var counted = RestOnly(farEnd.BaseUrl);
        var factory = new AriClientFactory(NullLoggerFactory.Instance);

        await using var first = await factory.CreateAndConnectAsync(unreachable);
        await using var second = await factory.CreateAndConnectAsync(counted);

        using (new AssertionScope())
        {
            first.State.Should().Be(AriConnectionState.RestOnly);
            second.State.Should().Be(AriConnectionState.RestOnly);
            farEnd.Dials.Should().Be(0, "the factory's connect dials nothing either");
        }
    }

    // ── State ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task State_ShouldReadRestOnly_WhenTheClientIsJustBuilt()
    {
        await using var sut = new AriClient(Options.Create(RestOnly("http://127.0.0.1:1")), NullLogger<AriClient>.Instance);

        using (new AssertionScope())
        {
            sut.State.Should().Be(AriConnectionState.RestOnly);
            sut.IsConnected.Should().BeFalse();
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldLeaveDisconnectedAndChangeNothingTheSecondTime_WhenCalledTwice()
    {
        await using var farEnd = new AriFarEnd();
        var sut = new AriClient(Options.Create(RestOnly(farEnd.BaseUrl)), NullLogger<AriClient>.Instance);

        await sut.DisposeAsync();
        var afterFirst = sut.State;
        await sut.DisposeAsync();
        var ping = async () => await sut.Asterisk.PingAsync();

        using (new AssertionScope())
        {
            afterFirst.Should().Be(AriConnectionState.Disconnected, "as on any client");
            sut.State.Should().Be(AriConnectionState.Disconnected);
            sut.IsConnected.Should().BeFalse();
            await ping.Should().ThrowAsync<ObjectDisposedException>("REST goes with the client");
            farEnd.Dials.Should().Be(0);
        }
    }

    // ── Subscribe ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Subscribe_ShouldThrowNamingTheOptionAndLeaveNoSubscription_WhenEventsAreOff()
    {
        var sut = new AriClient(Options.Create(RestOnly("http://127.0.0.1:1")), NullLogger<AriClient>.Instance);
        var observer = new CompletionProbe();

        var subscribe = () => sut.Subscribe(observer);

        using (new AssertionScope())
        {
            subscribe.Should().Throw<InvalidOperationException>().WithMessage("*EventsEnabled*");
            var dispose = async () => await sut.DisposeAsync();
            await dispose.Should().NotThrowAsync("a refused subscription leaves nothing to clean up");
            observer.Completed.Should().BeFalse("no subscription exists, so the end of the stream reaches nobody");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AriClientOptions RestOnly(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        Username = "asterisk",
        Password = "p4ss",
        Application = "test-app",
        EventsEnabled = false,
    };

    /// <summary>A port nothing listens on: bound, read and released here.</summary>
    private static string UnreachableBaseUrl()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return $"http://127.0.0.1:{((System.Net.IPEndPoint)probe.LocalEndpoint).Port}";
    }

    private sealed class CompletionProbe : IObserver<AriEvent>
    {
        public bool Completed { get; private set; }

        public void OnNext(AriEvent value)
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => Completed = true;
    }

    private sealed class FirstEvent(TaskCompletionSource<AriEvent> received) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value) => received.TrySetResult(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
