using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.TestInfrastructure.Wire;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A <see cref="RawAction"/> goes through every send path of an <see cref="AmiConnection"/>: it is written as
/// <c>Action: &lt;name&gt;</c>, its <c>ActionID</c> and its fields in the order added, and its answer is read by its
/// name. Answers are replayed from captures of four Asterisk versions (<see cref="AmiActionByNameWireCapture"/>)
/// wherever one exists; the two-entry <c>IAXpeers</c> listing is the one synthetic script, because no captured image
/// had an IAX peer.
/// </summary>
public sealed class AmiConnectionRawActionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    public static TheoryData<string> Versions() => [.. AmiActionByNameWireCapture.Versions];

    [Fact]
    public async Task SendActionAsync_ShouldWriteNameActionIdAndFieldsOnly_WhenRawDahdiActionIsSent()
    {
        await using var rig = await Rig.ConnectAsync();

        var (sent, _) = await rig.SendAsync(
            new RawAction("DAHDIDNDon") { ActionId = "raw-1" }.Add("DAHDIChannel", "1"),
            id => Frame("Error", id, ("Message", "Invalid/unknown command")));

        sent.Should().Be("Action: DAHDIDNDon\r\nActionID: raw-1\r\nDAHDIChannel: 1\r\n\r\n");
    }

    [Fact]
    public async Task SendActionAsync_ShouldWriteRepeatedKeysInOrder_WhenRawActionRepeatsAKey()
    {
        await using var rig = await Rig.ConnectAsync();

        var (sent, _) = await rig.SendAsync(
            new RawAction("Originate") { ActionId = "raw-2" }.Add("Variable", "a=1").Add("Variable", "b=2"),
            id => Frame("Success", id));

        sent.Should().Be("Action: Originate\r\nActionID: raw-2\r\nVariable: a=1\r\nVariable: b=2\r\n\r\n");
    }

    [Fact]
    public async Task SendActionAsync_ShouldWriteParenthesisedKeys_WhenRawActionUsesTheKeysAsteriskDocuments()
    {
        await using var rig = await Rig.ConnectAsync();

        var (sent, _) = await rig.SendAsync(
            new RawAction("AOCMessage") { ActionId = "raw-3" }.Add("UnitAmount(0)", "1").Add("UnitType(0)", "2"),
            id => Frame("Success", id));

        sent.Should().Be("Action: AOCMessage\r\nActionID: raw-3\r\nUnitAmount(0): 1\r\nUnitType(0): 2\r\n\r\n");
    }

    [Fact]
    public async Task SendActionAsync_ShouldWriteTheSameBytes_WhenRawPingAndTypedPingShareAnActionId()
    {
        await using var typedRig = await Rig.ConnectAsync();
        await using var rawRig = await Rig.ConnectAsync();

        var (typed, _) = await typedRig.SendAsync(new PingAction { ActionId = "same-1" }, id => Frame("Success", id));
        var (raw, _) = await rawRig.SendAsync(new RawAction("Ping") { ActionId = "same-1" }, id => Frame("Success", id));

        raw.Should().Be(typed).And.Be("Action: Ping\r\nActionID: same-1\r\n\r\n");
    }

    public static TheoryData<string, string, bool> PingCases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var version in AmiActionByNameWireCapture.Versions)
        {
            foreach (var name in new[] { "Ping", "pInG" })
            {
                data.Add(version, name, false);
                data.Add(version, name, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PingCases))]
    public async Task SendActionAsync_ShouldReturnPingResponseWithEveryHeader_WhenRawActionIsNamedPingInAnyCase(
        string version, string name, bool generic)
    {
        await using var rig = await Rig.ConnectAsync();
        var action = new RawAction(name);

        var (sent, response) = await rig.SendAsync(
            action, id => AmiActionByNameWireCapture.Reply(version, "i", id), generic ? typeof(PingResponse) : null);

        sent.Should().StartWith("Action: " + name + "\r\n", "the name is written in the letter case it was given");
        var ping = response.Should().BeOfType<PingResponse>().Which;
        ping.Response.Should().Be("Success");
        ping.ActionId.Should().Be(action.ActionId);
        ping.RawFields.Should().NotBeNull();
        ping.RawFields!.Keys.Should().BeEquivalentTo(HeaderKeys(AmiActionByNameWireCapture.Reply(version, "i", "x")));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task SendActionAsync_ShouldReturnBaseResponseWithEveryHeader_WhenRawActionIsUntyped(string version)
    {
        await using var rig = await Rig.ConnectAsync();

        var (sent, response) = await rig.SendAsync(
            new RawAction("LoggerRotate"), id => AmiActionByNameWireCapture.Reply(version, "ii", id));

        sent.Should().MatchRegex(@"^Action: LoggerRotate\r\nActionID: [^\r\n]+\r\n\r\n$");
        response.GetType().Should().Be<ManagerResponse>("the SDK types no LoggerRotate response");
        response.Response.Should().Be("Success");
        response.Message.Should().Be("Reloaded the logger and rotated log files");
        response.RawFields!.Keys.Should().BeEquivalentTo(HeaderKeys(AmiActionByNameWireCapture.Reply(version, "ii", "x")));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task SendActionAsync_ShouldReturnAsterisksError_WhenRawActionNameIsNotRegistered(string version)
    {
        await using var rig = await Rig.ConnectAsync();

        var (sent, response) = await rig.SendAsync(
            new RawAction("VerbaraNoSuchAction"), id => AmiActionByNameWireCapture.Reply(version, "viii", id));

        sent.Should().MatchRegex(@"^Action: VerbaraNoSuchAction\r\nActionID: [^\r\n]+\r\n\r\n$", "the SDK does not judge the name");
        response.Response.Should().Be("Error");
        response.Message.Should().StartWith("Invalid/unknown command: VerbaraNoSuchAction.");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task SendEventGeneratingActionAsync_ShouldEndAtPeerlistCompleteYieldingNothing_WhenCapturedIaxPeersListIsEmpty(
        string version)
    {
        await using var rig = await Rig.ConnectAsync();
        var outcome = new EventActionOutcome();

        var (sent, events) = await rig.ListAsync(
            new RawAction("IAXpeers"), id => AmiActionByNameWireCapture.Reply(version, "iv", id), outcome);

        sent.Should().MatchRegex(@"^Action: IAXpeers\r\nActionID: [^\r\n]+\r\n\r\n$");
        events.Should().BeEmpty("the captured list has no entry, and PeerlistComplete ends it without being yielded");
        outcome.Reported.Should().BeTrue();
        outcome.Rejection.Should().BeNull();
        outcome.SessionEnded.Should().BeFalse();
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldYieldEachPeerEntryInOrder_WhenIaxPeersListHasTwoEntries()
    {
        await using var rig = await Rig.ConnectAsync();

        var (_, events) = await rig.ListAsync(
            new RawAction("IAXpeers"),
            id => Concat(
                Frame("Success", id, ("EventList", "start"), ("Message", "Peer status list will follow")),
                EventFrame("PeerEntry", id, ("Channeltype", "IAX"), ("ObjectName", "trunk-a")),
                EventFrame("PeerEntry", id, ("Channeltype", "IAX"), ("ObjectName", "trunk-b")),
                EventFrame("PeerlistComplete", id, ("EventList", "Complete"), ("ListItems", "2"))),
            outcome: null);

        events.Select(e => e.EventType).Should().Equal("PeerEntry", "PeerEntry");
        events.Select(e => e.RawFields!["ObjectName"]).Should().Equal("trunk-a", "trunk-b");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task SendEventGeneratingActionAsync_ShouldYieldTheThreeCapturedEntriesInOrder_WhenDeviceStateListIsReplayed(
        string version)
    {
        await using var rig = await Rig.ConnectAsync();
        var reply = AmiActionByNameWireCapture.Reply(version, "v", "x");

        var (_, events) = await rig.ListAsync(
            new RawAction("DeviceStateList"), id => AmiActionByNameWireCapture.Reply(version, "v", id), outcome: null);

        events.Select(e => e.EventType).Should().Equal("DeviceStateChange", "DeviceStateChange", "DeviceStateChange");
        events.Select(e => e.RawFields!["Device"]).Should().Equal(Values(reply, "Device"),
            "every captured entry is yielded, in the order received");
    }

    /// <summary>The nine captured frames as a consumer writes them, independently of the fixture's bytes.</summary>
    private static RawAction CapturedWorkload(string frame) => frame switch
    {
        "i" => new RawAction("Ping"),
        "ii" => new RawAction("LoggerRotate"),
        "iii" => new RawAction("ListCategories").Add("Filename", "manager.conf"),
        "iv" => new RawAction("IAXpeers"),
        "v" => new RawAction("DeviceStateList"),
        "vi" => new RawAction("devicestatelist"),
        "vii" => new RawAction("DAHDIDNDon").Add("DAHDIChannel", "1"),
        "viii" => new RawAction("VerbaraNoSuchAction"),
        "ix" => new RawAction("FAXSessions"),
        _ => throw new ArgumentOutOfRangeException(nameof(frame), frame, "Unknown frame."),
    };

    public static TheoryData<string, string> CapturedFrames()
    {
        var data = new TheoryData<string, string>();
        foreach (var version in AmiActionByNameWireCapture.Versions)
        {
            foreach (var frame in AmiActionByNameWireCapture.Frames)
                data.Add(version, frame);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CapturedFrames))]
    public async Task SendActionAsync_ShouldWriteTheCapturedClientBytes_WhenTheCapturedWorkloadIsSentAsRawActions(
        string version, string frame)
    {
        await using var rig = await Rig.ConnectAsync();
        var action = CapturedWorkload(frame);
        action.ActionId = AmiActionByNameWireCapture.CapturedActionId(version, frame);

        var (sent, _) = await rig.SendAsync(action, id => AmiActionByNameWireCapture.Reply(version, frame, id));

        Encoding.Latin1.GetBytes(sent).Should().Equal(AmiActionByNameWireCapture.ClientFrame(version, frame),
            "the SDK writes, byte for byte, what a raw-socket client wrote to Asterisk {0}", version);
    }

    private static IReadOnlyList<string> HeaderKeys(byte[] message) =>
    [
        .. Encoding.Latin1.GetString(message).Split("\r\n")
            .Where(static l => l.Contains(':', StringComparison.Ordinal))
            .Select(static l => l[..l.IndexOf(':', StringComparison.Ordinal)]),
    ];

    private static IReadOnlyList<string> Values(byte[] stream, string key) =>
    [
        .. Encoding.Latin1.GetString(stream).Split("\r\n")
            .Where(l => l.StartsWith(key + ": ", StringComparison.Ordinal))
            .Select(l => l[(key.Length + 2)..]),
    ];

    private static byte[] Frame(string status, string actionId, params (string Key, string Value)[] fields) =>
        Message(("Response", status), [("ActionID", actionId), .. fields]);

    private static byte[] EventFrame(string name, string actionId, params (string Key, string Value)[] fields) =>
        Message(("Event", name), [("ActionID", actionId), .. fields]);

    private static byte[] Message((string Key, string Value) first, (string Key, string Value)[] rest)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{first.Key}: {first.Value}\r\n");
        foreach (var (key, value) in rest)
            sb.Append(CultureInfo.InvariantCulture, $"{key}: {value}\r\n");
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    /// <summary>One connected <see cref="AmiConnection"/> over in-memory pipes, and a fake server that answers it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Pipe _serverToClient = new();
        private readonly Pipe _clientToServer = new();
        private readonly CancellationTokenSource _cts = new(Bound);

        private Rig()
        {
            var socket = Substitute.For<ISocketConnection>();
            socket.Input.Returns(_serverToClient.Reader);
            socket.Output.Returns(_clientToServer.Writer);
            socket.IsConnected.Returns(true);
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
            socket.ConnectAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.CompletedTask);
#pragma warning restore CA2012
            var factory = Substitute.For<ISocketConnectionFactory>();
            factory.Create().Returns(socket);

            Connection = new AmiConnection(
                Options.Create(new AmiConnectionOptions
                {
                    Hostname = "localhost",
                    Port = 5038,
                    Username = "admin",
                    Password = "secret",
                    AutoReconnect = false,
                    EnableHeartbeat = false,
                    DefaultResponseTimeout = TimeSpan.FromSeconds(5),
                    DefaultEventTimeout = TimeSpan.FromSeconds(5),
                }),
                factory,
                NullLogger<AmiConnection>.Instance);
        }

        public AmiConnection Connection { get; }

        public static async Task<Rig> ConnectAsync()
        {
            var rig = new Rig();
            var login = rig.LoginAsync();
            await rig.Connection.ConnectAsync(rig._cts.Token);
            await login;
            return rig;
        }

        /// <summary>Sends <paramref name="action"/>; the server reads it and answers with <paramref name="answer"/>.</summary>
        public async Task<(string Sent, ManagerResponse Response)> SendAsync(
            ManagerAction action, Func<string, byte[]> answer, Type? typed = null)
        {
            var server = AnswerOnceAsync(answer);
            var response = typed == typeof(PingResponse)
                ? await Connection.SendActionAsync<PingResponse>(action, _cts.Token)
                : await Connection.SendActionAsync(action, _cts.Token);
            return (await server, response);
        }

        /// <summary>Sends <paramref name="action"/> as an event-generating action and collects what it yields.</summary>
        public async Task<(string Sent, List<ManagerEvent> Events)> ListAsync(
            ManagerAction action, Func<string, byte[]> answer, EventActionOutcome? outcome)
        {
            var server = AnswerOnceAsync(answer);
            var events = new List<ManagerEvent>();
            var sequence = outcome is null
                ? Connection.SendEventGeneratingActionAsync(action, _cts.Token)
                : Connection.SendEventGeneratingActionAsync(action, outcome, _cts.Token);
            await foreach (var evt in sequence)
                events.Add(evt);
            return (await server, events);
        }

        private Task<string> AnswerOnceAsync(Func<string, byte[]> answer) => Task.Run(async () =>
        {
            var text = await ReadActionAsync(_clientToServer.Reader, _cts.Token);
            await _serverToClient.Writer.WriteAsync(answer(ExtractActionId(text)), _cts.Token);
            await _serverToClient.Writer.FlushAsync(_cts.Token);
            return text;
        }, _cts.Token);

        private async Task LoginAsync()
        {
            var ct = _cts.Token;
            await _serverToClient.Writer.WriteAsync(Encoding.ASCII.GetBytes("Asterisk Call Manager/6.0.0\r\n"), ct);
            foreach (var extra in new (string, string)[] { ("Challenge", "abc123"), ("Message", "Authentication accepted"), ("AsteriskVersion", "20.0.0") })
            {
                var action = await ReadActionAsync(_clientToServer.Reader, ct);
                await _serverToClient.Writer.WriteAsync(Frame("Success", ExtractActionId(action), extra), ct);
                await _serverToClient.Writer.FlushAsync(ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _serverToClient.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await Connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            _cts.Dispose();
        }

        private static async Task<string> ReadActionAsync(PipeReader reader, CancellationToken ct)
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var text = Encoding.UTF8.GetString(buffer);
                var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    reader.AdvanceTo(buffer.GetPosition(idx + 4));
                    return text[..(idx + 4)];
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) return text;
            }
        }

        private static string ExtractActionId(string actionText)
        {
            var match = Regex.Match(actionText, @"ActionID:\s*(.+?)\r?\n", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }
    }
}
