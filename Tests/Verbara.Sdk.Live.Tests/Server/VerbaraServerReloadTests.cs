using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// Binds what <c>VerbaraServer</c> does to the channel table when it reloads state from Asterisk —
/// the buffering half of ADR-0062, design D1. The reload reads the whole snapshot into a buffer and
/// hands that buffer to <c>ChannelManager.ReconcileWithSnapshot</c>; it no longer calls
/// <c>Channels.Clear()</c>, which emptied the table without announcing anything.
///
/// <para>The failure direction is the requirement these tests exist for: a snapshot that throws, or
/// that stops early because the caller cancelled, MUST leave every held channel alone. Nothing may
/// be removed from an unfinished snapshot, because absence from it is not evidence that a channel is
/// gone — and ending a live call by mistake is worse than the defect the reconciliation removes.</para>
///
/// <para>These drive the server alone: no session manager, so what is measured here is the table and
/// the <c>ChannelRemoved</c> / <c>ChannelAdded</c> events. The session-level consequence is measured
/// in <c>Verbara.Sdk.Sessions.FunctionalTests</c>.</para>
/// </summary>
public sealed class VerbaraServerReloadTests : IAsyncDisposable
{
    private const string HeldUid = "1700000000.1";
    private const string HeldName = "PJSIP/2000-0001";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _sut;
    private readonly ReloadLogger _log = new();

    private readonly List<AsteriskChannel> _added = [];
    private readonly List<AsteriskChannel> _removed = [];

    /// <summary>How the fake answers a <c>StatusAction</c>: the entries, then the ending.</summary>
    private IReadOnlyList<StatusEvent> _statusReply = [];
    private SnapshotEnding _ending = SnapshotEnding.Completes;
    private CancellationTokenSource? _cancelAfterFirstEntry;

    private enum SnapshotEnding
    {
        /// <summary>The enumeration runs out normally — a snapshot that can be trusted.</summary>
        Completes,

        /// <summary>The enumeration throws after its first entry — a reload that failed midway.</summary>
        ThrowsMidway,

        /// <summary>
        /// The enumeration stops early after cancelling the caller's token, without throwing. A
        /// truncated sequence that reads as a completed one is the trap the reload must not fall
        /// into: it looks like "Asterisk has only this one channel".
        /// </summary>
        TruncatesOnCancellation
    }

    public VerbaraServerReloadTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0)));

        _sut = new VerbaraServer(_connection, _log.For<VerbaraServer>());
        _sut.Channels.ChannelAdded += _added.Add;
        _sut.Channels.ChannelRemoved += _removed.Add;
    }

    private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();

        if (action is not StatusAction)
        {
            if (action is AgentsAction)
                _log.ReloadFinished.TrySetResult();
            yield break;
        }

        var delivered = 0;
        foreach (var status in _statusReply)
        {
            yield return status;
            delivered++;

            if (delivered != 1)
                continue;

            if (_ending == SnapshotEnding.ThrowsMidway)
                throw new IOException("the AMI socket died halfway through the Status snapshot");

            if (_ending == SnapshotEnding.TruncatesOnCancellation)
            {
                await _cancelAfterFirstEntry!.CancelAsync();
                yield break;
            }
        }
    }

    /// <summary>
    /// Holds one channel, the way a live <c>NewChannel</c> event would, and starts the server —
    /// <c>StartAsync</c> is where <c>Reconnected</c> is subscribed, so a server that was only
    /// constructed never hears a reconnect at all.
    /// </summary>
    private async Task GivenAStartedServerHolding(params (string UniqueId, string Name)[] channels)
    {
        await _sut.StartAsync();
        foreach (var (uniqueId, name) in channels)
            _sut.Channels.OnNewChannel(uniqueId, name, ChannelState.Up, linkedId: "linked-1");

        _added.Clear();
        _removed.Clear();
    }

    /// <summary>
    /// Raises <c>Reconnected</c> and waits for the reload to finish — either at its last action
    /// (<c>AgentsAction</c>) or at the log line <c>OnReconnected</c>'s catch-all writes. Both are
    /// needed: <c>OnReconnected</c> is <c>async void</c>, so a failed reload never reaches the last
    /// action and would otherwise be indistinguishable from a reload that never started.
    /// </summary>
    private async Task WhenTheConnectionReconnects(params StatusEvent[] channelsAsteriskStillHas)
    {
        _statusReply = channelsAsteriskStillHas;

        // StartAsync ran its own load and already tripped the "finished" signal, so it is rearmed
        // here. Without this the wait returns instantly and every assertion below runs against a
        // reload that has not happened yet — measured 2026-09-25: the empty-snapshot test passed
        // its removal assertion nowhere and reported "1 channel(s) held ... ChannelRemoved []".
        _log.RearmReloadSignals();
        _connection.Reconnected += Raise.Event<Action>();

        // async void OnReconnected cannot be awaited, so the wait is on its two log-line signals.
        // fence-allow: GUARD-TIMEOUT — bounds that wait so a reload that never ran reports the log
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var finished = await Task.WhenAny(_log.ReloadFinished.Task, _log.ReloadFailed.Task, timeout);

        ReferenceEquals(finished, timeout).Should().BeFalse(
            $"the reconnect reload never finished. Server log:{Environment.NewLine}{_log}");
    }

    /// <summary>
    /// One <c>Status</c> entry shaped the way Asterisk really answers: the state arrives as the
    /// numeric <c>ChannelState</c> header alongside its text <c>ChannelStateDesc</c>, both in
    /// <c>RawFields</c>. <c>StatusEvent.State</c> is deliberately left unset — no supported version
    /// sends a <c>State:</c> header, so a fixture that filled it would be testing a wire this SDK
    /// never sees (ADR-0062, design D5).
    /// </summary>
    private static StatusEvent Leg(string uniqueId, string channel) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ChannelState"] = "6",       // AST_STATE_UP, exactly as the frame carries it
            ["ChannelStateDesc"] = "Up",
        },
    };

    private string Describe() =>
        $"{_sut.Channels.ChannelCount} channel(s) held [" +
        string.Join(" | ", _sut.Channels.ActiveChannels.Select(c => $"uid={c.UniqueId} name={c.Name}")) +
        $"]; ChannelAdded [{string.Join(", ", _added.Select(c => c.UniqueId))}]" +
        $"; ChannelRemoved [{string.Join(", ", _removed.Select(c => c.UniqueId))}]";

    // --- the reload is a reconciliation, not a wipe ---------------------------------------------

    [Fact]
    public async Task OnReconnected_ShouldRemoveTheHeldChannelAndAnnounceIt_WhenTheReloadReturnsNothing()
    {
        await GivenAStartedServerHolding((HeldUid, HeldName));

        await WhenTheConnectionReconnects();

        _sut.Channels.ChannelCount.Should().Be(0,
            $"Asterisk no longer has the channel, so the table must not keep it. Measured: {Describe()}");
        _removed.Should().ContainSingle(
            "Clear() emptied the table in silence, which is why a consumer holding call state was "
            + $"never told the call ended. Measured: {Describe()}")
            .Which.UniqueId.Should().Be(HeldUid);
    }

    [Fact]
    public async Task OnReconnected_ShouldKeepTheHeldChannelAndAnnounceNothing_WhenTheReloadStillReportsIt()
    {
        await GivenAStartedServerHolding((HeldUid, HeldName));
        var before = _sut.Channels.GetByUniqueId(HeldUid);

        await WhenTheConnectionReconnects(Leg(HeldUid, HeldName));

        _sut.Channels.GetByUniqueId(HeldUid).Should().BeSameAs(before,
            "a channel the snapshot still contains never went away; re-admitting it would discard "
            + $"its correlation and its relational state. Measured: {Describe()}");
        _removed.Should().BeEmpty($"the channel is still live. Measured: {Describe()}");
        _added.Should().BeEmpty($"it did not newly appear either. Measured: {Describe()}");
    }

    [Fact]
    public async Task OnReconnected_ShouldAdmitTheChannelAndAnnounceIt_WhenTheReloadReportsOneNotHeld()
    {
        await GivenAStartedServerHolding((HeldUid, HeldName));

        await WhenTheConnectionReconnects(
            Leg(HeldUid, HeldName),
            Leg("1700000000.9", "PJSIP/9000-0009"));

        _added.Should().ContainSingle(
            $"only the channel that started during the outage is new. Measured: {Describe()}")
            .Which.UniqueId.Should().Be("1700000000.9");
        _removed.Should().BeEmpty();
    }

    // --- the failure direction: a snapshot that cannot be trusted ends nothing -------------------

    [Fact]
    public async Task OnReconnected_ShouldRemoveNothing_WhenTheSnapshotEnumerationThrowsMidway()
    {
        await GivenAStartedServerHolding((HeldUid, HeldName), ("1700000000.2", "PJSIP/3000-0002"));
        _ending = SnapshotEnding.ThrowsMidway;

        // Asterisk starts answering — one channel arrives — and then the socket dies. The entry
        // that did arrive says nothing about the channels the snapshot never reached.
        await WhenTheConnectionReconnects(
            Leg(HeldUid, HeldName),
            Leg("1700000000.2", "PJSIP/3000-0002"));

        _removed.Should().BeEmpty(
            "absence from an unfinished snapshot is not evidence that a channel is gone; a removal "
            + $"here would end a live call. Measured: {Describe()}");
        _sut.Channels.ActiveChannels.Select(c => c.UniqueId).Should()
            .BeEquivalentTo([HeldUid, "1700000000.2"],
                $"a failed reload must mutate nothing at all. Measured: {Describe()}");
        _added.Should().BeEmpty(
            "the buffer is discarded with the exception, so not even the entry that did arrive is "
            + $"applied. Measured: {Describe()}");
        _log.ReloadFailed.Task.IsCompleted.Should().BeTrue(
            "OnReconnected swallows the exception into a log line — that line is the only evidence "
            + "a consumer or an operator gets that the reload failed");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldThrowAndRemoveNothing_WhenTheSnapshotEnumerationThrowsMidway()
    {
        await GivenAStartedServerHolding((HeldUid, HeldName), ("1700000000.2", "PJSIP/3000-0002"));
        _ending = SnapshotEnding.ThrowsMidway;
        _statusReply = [Leg(HeldUid, HeldName), Leg("1700000000.2", "PJSIP/3000-0002")];

        var reload = async () => await _sut.RequestInitialStateAsync();

        await reload.Should().ThrowAsync<IOException>(
            "the awaitable entry point surfaces the failure instead of swallowing it; only "
            + "OnReconnected's async-void catch-all turns it into a log line");
        _removed.Should().BeEmpty($"nothing was proven gone. Measured: {Describe()}");
        _sut.Channels.ChannelCount.Should().Be(2, $"Measured: {Describe()}");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldRemoveNothing_WhenTheSnapshotIsTruncatedByCancellation()
    {
        await GivenAStartedServerHolding((HeldUid, HeldName), ("1700000000.2", "PJSIP/3000-0002"));
        using var cts = new CancellationTokenSource();
        _cancelAfterFirstEntry = cts;
        _ending = SnapshotEnding.TruncatesOnCancellation;
        _statusReply = [Leg(HeldUid, HeldName), Leg("1700000000.2", "PJSIP/3000-0002")];

        var reload = async () => await _sut.RequestInitialStateAsync(cts.Token);

        await reload.Should().ThrowAsync<OperationCanceledException>(
            "an enumeration that honours the token by stopping quietly hands back a truncated "
            + "snapshot that reads as complete; cancellation is not completion, so the reload must "
            + "refuse it rather than reconcile against it");
        _removed.Should().BeEmpty(
            "reconciling the truncated snapshot would have removed the channel it never reached — "
            + $"a live call ended by a reload that did not finish. Measured: {Describe()}");
        _sut.Channels.ChannelCount.Should().Be(2, $"Measured: {Describe()}");
    }

    // --- what the buffer has to carry -----------------------------------------------------------

    [Fact]
    public async Task RequestInitialStateAsync_ShouldCarryEveryFieldTheSnapshotReports_WhenAChannelIsAdmitted()
    {
        _statusReply =
        [
            new StatusEvent
            {
                UniqueId = "1700000000.7",
                Channel = "PJSIP/trunk-0007",
                Extension = "800",
                RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ChannelState"] = "4",           // AST_STATE_RING
                    ["ChannelStateDesc"] = "Ring",
                    ["CallerIDNum"] = "5551234",
                    ["CallerIDName"] = "Ada Lovelace",
                    ["Context"] = "from-trunk",
                },
            }
        ];

        await _sut.RequestInitialStateAsync();

        var admitted = _sut.Channels.GetByUniqueId("1700000000.7");
        admitted.Should().NotBeNull($"Measured: {Describe()}");
        admitted!.Name.Should().Be("PJSIP/trunk-0007");
        admitted.State.Should().Be(ChannelState.Ring);
        admitted.CallerIdNum.Should().Be("5551234");
        admitted.CallerIdName.Should().Be("Ada Lovelace");
        admitted.Context.Should().Be("from-trunk",
            "Context reaches the table only through RawFields, and CallSessionManager infers a "
            + "call's direction from it — a buffer narrower than OnNewChannel's parameters would "
            + "drop it silently and flip every loaded call's direction");
        admitted.Extension.Should().Be("800");
        _sut.Channels.GetByName("PJSIP/trunk-0007").Should().BeSameAs(admitted,
            "the name index is the second half of the table");
    }

    // --- the headers Asterisk actually sends (design D5) -----------------------------------------
    //
    // Measured 2026-09-24 on 18.26.4, 20.20.1, 22.9.0 and 23.4.1: no Status frame on any supported
    // version carries a State: or a CallerID: header, and the header set is byte-for-byte identical
    // across the four. What every version does carry is ChannelState (numeric) with
    // ChannelStateDesc (text), and CallerIDNum with CallerIDName. Reading StatusEvent.State and
    // StatusEvent.CallerId therefore landed every reloaded channel as Unknown with no caller id, on
    // every version, today.

    [Fact]
    public async Task RequestInitialStateAsync_ShouldAdmitTheChannelInTheStateAsteriskReported_WhenTheSnapshotSaysItIsAnswered()
    {
        _statusReply =
        [
            new StatusEvent
            {
                UniqueId = "1700000000.7",
                Channel = "PJSIP/trunk-0007",
                RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ChannelState"] = "6",           // AST_STATE_UP
                    ["ChannelStateDesc"] = "Up",
                },
            }
        ];

        await _sut.RequestInitialStateAsync();

        var admitted = _sut.Channels.GetByUniqueId("1700000000.7");
        admitted.Should().NotBeNull($"Measured: {Describe()}");
        admitted!.State.Should().Be(ChannelState.Up,
            "Asterisk reported this channel as answered; a reload that reads the header it really "
            + "sends must hold it in that state");
        admitted.State.Should().NotBe(ChannelState.Unknown,
            "Unknown is what reading the State: header produced for every channel on every "
            + "version, which is the defect this binds");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldCarryTheCallingNumber_WhenTheSnapshotReportsOne()
    {
        _statusReply =
        [
            new StatusEvent
            {
                UniqueId = "1700000000.7",
                Channel = "PJSIP/trunk-0007",
                RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ChannelState"] = "6",
                    ["ChannelStateDesc"] = "Up",
                    ["CallerIDNum"] = "5551234",
                    ["CallerIDName"] = "Ada Lovelace",
                },
            }
        ];

        await _sut.RequestInitialStateAsync();

        var admitted = _sut.Channels.GetByUniqueId("1700000000.7");
        admitted.Should().NotBeNull($"Measured: {Describe()}");
        admitted!.CallerIdNum.Should().Be("5551234",
            "the caller identity survives the reload; CallerIDNum is the header that carries it");
        admitted.CallerIdName.Should().Be("Ada Lovelace",
            "CallerIDName travels the same route and is the other half of the identity");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldAdmitTheChannelAsUnknown_WhenTheSnapshotCarriesNoStateHeaderAtAll()
    {
        _statusReply =
        [
            new StatusEvent
            {
                UniqueId = "1700000000.7",
                Channel = "PJSIP/trunk-0007",
                RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Context"] = "from-trunk",
                },
            }
        ];

        await _sut.RequestInitialStateAsync();

        var admitted = _sut.Channels.GetByUniqueId("1700000000.7");
        admitted.Should().NotBeNull(
            "a channel whose state Asterisk did not report is still a channel Asterisk has; the "
            + $"reload admits it rather than rejecting it. Measured: {Describe()}");
        admitted!.State.Should().Be(ChannelState.Unknown,
            "defaulting is correct when the value is genuinely absent — what was wrong was "
            + "defaulting a value that could never arrive");
        _added.Should().ContainSingle().Which.UniqueId.Should().Be("1700000000.7");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldIgnoreTheHeadersNoVersionSends_WhenStatusEventCarriesThemAnyway()
    {
        // The negative control for design D5: State and CallerId are set here to values that
        // contradict the wire, so a reload that still read them would report Up / 9999999. Nothing
        // on any measured version populates them, and nothing in the reload may consume them.
        _statusReply =
        [
            new StatusEvent
            {
                UniqueId = "1700000000.7",
                Channel = "PJSIP/trunk-0007",
                State = nameof(ChannelState.Up),
                CallerId = "9999999",
                RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ChannelState"] = "4",           // AST_STATE_RING
                    ["ChannelStateDesc"] = "Ring",
                    ["CallerIDNum"] = "5551234",
                },
            }
        ];

        await _sut.RequestInitialStateAsync();

        var admitted = _sut.Channels.GetByUniqueId("1700000000.7");
        admitted.Should().NotBeNull($"Measured: {Describe()}");
        admitted!.State.Should().Be(ChannelState.Ring,
            "the wire header wins; StatusEvent.State is a property no Asterisk version fills and "
            + "the reload must not read it");
        admitted.CallerIdNum.Should().Be("5551234",
            "likewise StatusEvent.CallerId — the calling number arrives as CallerIDNum");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldAdmitTheNumberedState_WhenAsterisksTextSpellingWouldNotParse()
    {
        // Why the numeric header and not the text one. ChannelState is DEFINED as Asterisk's
        // numeric values (Down = 0 .. Unknown = 10, contiguous), so the numeric header round-trips
        // by construction. The text header carries no such guarantee: its spellings are Asterisk's
        // prose, not this enum's member names, and "Rsrvd" — the spelling for AST_STATE_RESERVED —
        // does not parse, which the assertion below measures. Only "Up" (state 6) was observed on
        // the wire by task 3.3; that three spellings diverge is read from Asterisk's ast_state2str,
        // NOT measured here, and the numeric choice does not depend on it being exactly three.
        Enum.TryParse<ChannelState>("Rsrvd", out _).Should().BeFalse(
            "this spelling is not a member of ChannelState, so a reload parsing ChannelStateDesc "
            + "would report Unknown for whatever state Asterisk spells this way");

        _statusReply =
        [
            new StatusEvent
            {
                UniqueId = "1700000000.7",
                Channel = "PJSIP/trunk-0007",
                RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ChannelState"] = "1",           // AST_STATE_RESERVED
                    ["ChannelStateDesc"] = "Rsrvd",
                },
            }
        ];

        await _sut.RequestInitialStateAsync();

        _sut.Channels.GetByUniqueId("1700000000.7")!.State.Should().Be(ChannelState.Reserved,
            $"the numeric header is exact where the text one is not. Measured: {Describe()}");
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldAdmitTheChannelAsUnknown_WhenTheStateHeaderIsNotANumberThisSdkKnows()
    {
        _statusReply =
        [
            new StatusEvent
            {
                UniqueId = "1700000000.7",
                Channel = "PJSIP/trunk-0007",
                RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ChannelState"] = "42",          // a state this SDK has no member for
                    ["ChannelStateDesc"] = "Teleported",
                },
            }
        ];

        await _sut.RequestInitialStateAsync();

        _sut.Channels.GetByUniqueId("1700000000.7")!.State.Should().Be(ChannelState.Unknown,
            "a value outside the enum is genuinely unknown to this SDK and must say so, not be "
            + $"cast into a ChannelState that names nothing. Measured: {Describe()}");
    }

    public async ValueTask DisposeAsync() => await _sut.DisposeAsync();

    /// <summary>
    /// Records every line the server logs and completes one of two tasks when the reload ends —
    /// the last action for a reload that finished, the catch-all's line for one that failed.
    /// </summary>
    private sealed class ReloadLogger
    {
        private readonly List<string> _lines = [];

        public TaskCompletionSource ReloadFinished { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReloadFailed { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Arms both signals for the next reload. One signal per load: the initial
        /// <c>StartAsync</c> completes "finished" too, and a signal left completed makes the next
        /// wait return before the reload it is waiting for has started.
        /// </summary>
        public void RearmReloadSignals()
        {
            ReloadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReloadFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ILogger<T> For<T>() => new Sink<T>(this);

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }

            if (line.Contains("Reconnect reload failed", StringComparison.Ordinal))
                ReloadFailed.TrySetResult();
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.Count == 0
                    ? "  (the server logged nothing)"
                    : "  " + string.Join($"{Environment.NewLine}  ", _lines);
            }
        }

        private sealed class Sink<T>(ReloadLogger owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                owner.Add(exception is null
                    ? $"[{logLevel}] {message}"
                    : $"[{logLevel}] {message} -> {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
