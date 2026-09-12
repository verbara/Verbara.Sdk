using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Ami.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The protocol writer rejects a line break anywhere in an action before any byte reaches the
/// transport. These tests pin what that means for a connection: every public send path surfaces the
/// <see cref="ArgumentException"/>, leaves no pending registration or held lock behind, and the next
/// action on the same connection completes.
/// </summary>
public sealed class AmiConnectionLineBreakTests : IAsyncDisposable
{
    // Rejected strings are built from these runs so a fragment of them in a message is detectable.
    private const string Head = "7qz9xw";
    private const string Tail = "3jkvb5";

    private readonly Pipe _serverToClient = new();
    private readonly Pipe _clientToServer = new();
    private readonly AmiConnection _sut;

    public AmiConnectionLineBreakTests()
    {
        _sut = CreateConnection(_serverToClient, _clientToServer, username: "admin");
    }

    public async ValueTask DisposeAsync()
    {
        await _serverToClient.Writer.CompleteAsync();
        await _clientToServer.Reader.CompleteAsync();
        await _sut.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ConnectAsync_ShouldThrowArgumentExceptionWithoutSendingLogin_WhenUsernameContainsLineBreak(string lineBreak)
    {
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        var connection = CreateConnection(serverToClient, clientToServer, username: Head + lineBreak + Tail);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var server = Task.Run(async () =>
            {
                await WriteBytesAsync(serverToClient.Writer, "Asterisk Call Manager/6.0.0\r\n");
                var challenge = await ReadActionAsync(clientToServer.Reader, cts.Token);
                await WriteResponseAsync(serverToClient.Writer, "Success", ExtractActionId(challenge), [new("Challenge", "abc123")]);
                return challenge;
            }, cts.Token);

            var act = async () => await connection.ConnectAsync(cts.Token);

            var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
            thrown.Message.Should().NotContain(Head).And.NotContain(Tail);
            (await server).Should().StartWith("Action: Challenge\r\n", "the challenge is sent before the login");
            clientToServer.Writer.UnflushedBytes.Should().Be(0, "no byte of the Login action may reach the transport");
            clientToServer.Reader.TryRead(out _).Should().BeFalse("no byte of the Login action may reach the transport");
        }
        finally
        {
            await serverToClient.Writer.CompleteAsync();
            await clientToServer.Reader.CompleteAsync();
            await connection.DisposeAsync();
        }
    }

    private static AmiConnection CreateConnection(Pipe serverToClient, Pipe clientToServer, string username)
    {
        var socketConnection = Substitute.For<ISocketConnection>();
        socketConnection.Input.Returns(serverToClient.Reader);
        socketConnection.Output.Returns(clientToServer.Writer);
        socketConnection.IsConnected.Returns(true);
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        socketConnection.ConnectAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
#pragma warning restore CA2012

        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        socketFactory.Create().Returns(socketConnection);

        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 5038,
            Username = username,
            Password = "secret",
            AutoReconnect = false,
            EnableHeartbeat = false,
            DefaultResponseTimeout = TimeSpan.FromSeconds(5)
        });

        return new AmiConnection(options, socketFactory, NullLogger<AmiConnection>.Instance);
    }

    public static TheoryData<string, string> LineBreakPlacements()
    {
        var data = new TheoryData<string, string>();
        foreach (var placement in new[] { "ActionID", "field value", "extra field value", "numbered extra field value", "AsyncAGI command" })
        {
            foreach (var lineBreak in new[] { "\r", "\n", "\r\n" })
                data.Add(placement, lineBreak);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LineBreakPlacements))]
    public async Task SendActionAsync_ShouldThrowArgumentExceptionAndStayUsable_WhenActionContainsLineBreak(
        string placement, string lineBreak)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ConnectWithSuccessfulLoginAsync(cts.Token);
        var action = CreateActionWithLineBreak(placement, lineBreak);

        var act = async () => await _sut.SendActionAsync(action, cts.Token);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        await ShouldLeaveNothingBehindAndStayUsableAsync(thrown, cts.Token);
    }

    [Theory]
    [MemberData(nameof(LineBreakPlacements))]
    public async Task SendActionAsyncOfTResponse_ShouldThrowArgumentExceptionAndStayUsable_WhenActionContainsLineBreak(
        string placement, string lineBreak)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ConnectWithSuccessfulLoginAsync(cts.Token);
        var action = CreateActionWithLineBreak(placement, lineBreak);

        var act = async () => await _sut.SendActionAsync<ManagerResponse>(action, cts.Token);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        await ShouldLeaveNothingBehindAndStayUsableAsync(thrown, cts.Token);
    }

    [Theory]
    [MemberData(nameof(LineBreakPlacements))]
    public async Task SendEventGeneratingActionAsync_ShouldThrowArgumentExceptionAndStayUsable_WhenActionContainsLineBreak(
        string placement, string lineBreak)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ConnectWithSuccessfulLoginAsync(cts.Token);
        var action = CreateActionWithLineBreak(placement, lineBreak);

        var act = async () =>
        {
            var events = new List<ManagerEvent>();
            await foreach (var evt in _sut.SendEventGeneratingActionAsync(action, cts.Token))
                events.Add(evt);
        };

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        await ShouldLeaveNothingBehindAndStayUsableAsync(thrown, cts.Token);
    }

    private async Task ConnectWithSuccessfulLoginAsync(CancellationToken ct)
    {
        var login = SimulateSuccessfulLoginAsync(ct);
        await _sut.ConnectAsync(ct);
        await login;
    }

    private async Task ShouldLeaveNothingBehindAndStayUsableAsync(ArgumentException thrown, CancellationToken ct)
    {
        thrown.Message.Should().NotContain(Head).And.NotContain(Tail);

        _clientToServer.Writer.UnflushedBytes.Should().Be(0, "no byte of the rejected action may reach the transport");
        _clientToServer.Reader.TryRead(out _).Should().BeFalse("no byte of the rejected action may reach the transport");
        PendingActions(_sut).Should().BeEmpty("a rejected action must not stay registered for a response");
        PendingEventActions(_sut).Should().BeEmpty("a rejected action must not stay registered for events");
        ActionNames(_sut).Should().BeEmpty("a rejected action must not leave its ActionID behind");
        WriteLock(_sut).CurrentCount.Should().Be(1, "the write lock must be released");

        // The next action on the same connection completes, and it is all that reaches the wire.
        var server = Task.Run(async () =>
        {
            var text = await ReadActionAsync(_clientToServer.Reader, ct);
            await WriteResponseAsync(_serverToClient.Writer, "Success", ExtractActionId(text), [new("Ping", "Pong")]);
            return text;
        }, ct);

        var response = await _sut.SendActionAsync(new PingAction(), ct);
        var sent = await server;

        response.Response.Should().Be("Success");
        sent.Should().MatchRegex(@"^Action: Ping\r\nActionID: [^\r\n]+\r\n\r\n$");
    }

    private static ManagerAction CreateActionWithLineBreak(string placement, string lineBreak)
    {
        var rejected = Head + lineBreak + Tail;
        switch (placement)
        {
            case "ActionID":
                return new StatusAction { ActionId = rejected };
            case "field value":
                return new CommandAction { Command = rejected };
            case "extra field value":
                var originate = new OriginateAction { Channel = "PJSIP/2000", Context = "default", Exten = "100", Priority = 1 };
                originate.SetVariable("Queue", rejected);
                return originate;
            case "numbered extra field value":
                return new UpdateConfigAction { SrcFilename = "extensions.conf", DstFilename = "extensions.conf" }
                    .AddNewCategory("general")
                    .AddAppend("general", "static", rejected);
            case "AsyncAGI command":
                return new AgiAction { Channel = "PJSIP/2000-00000001", Command = rejected, CommandId = "cmd-1" };
            default:
                throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown placement.");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pendingActions")]
    private static extern ref ConcurrentDictionary<string, TaskCompletionSource<AmiMessage>> PendingActions(AmiConnection connection);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pendingEventActions")]
    private static extern ref ConcurrentDictionary<string, ResponseEventCollector> PendingEventActions(AmiConnection connection);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_actionNames")]
    private static extern ref ConcurrentDictionary<string, string> ActionNames(AmiConnection connection);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_writeLock")]
    private static extern ref SemaphoreSlim WriteLock(AmiConnection connection);

    /// <summary>
    /// Simulates a successful AMI login sequence from the server side.
    /// Reads client's actions to extract actual ActionIDs and echoes them back.
    /// </summary>
    private async Task SimulateSuccessfulLoginAsync(CancellationToken ct)
    {
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        await WriteBytesAsync(writer, "Asterisk Call Manager/6.0.0\r\n");

        var challengeAction = await ReadActionAsync(reader, ct);
        await WriteResponseAsync(writer, "Success", ExtractActionId(challengeAction), [new("Challenge", "abc123")]);

        var loginAction = await ReadActionAsync(reader, ct);
        await WriteResponseAsync(writer, "Success", ExtractActionId(loginAction), [new("Message", "Authentication accepted")]);

        var coreAction = await ReadActionAsync(reader, ct);
        await WriteResponseAsync(writer, "Success", ExtractActionId(coreAction), [new("AsteriskVersion", "20.0.0")]);
    }

    private static async Task WriteBytesAsync(PipeWriter writer, string data)
    {
        await writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await writer.FlushAsync();
    }

    private static async Task WriteResponseAsync(PipeWriter writer, string status, string actionId,
        List<KeyValuePair<string, string>>? fields = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Response: {status}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"ActionID: {actionId}\r\n");
        if (fields is not null)
        {
            foreach (var kv in fields)
                sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}: {kv.Value}\r\n");
        }
        sb.Append("\r\n");
        await WriteBytesAsync(writer, sb.ToString());
    }

    /// <summary>Reads a complete AMI action message (terminated by \r\n\r\n) from the pipe.</summary>
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
                var action = text[..(idx + 4)];
                reader.AdvanceTo(buffer.GetPosition(idx + 4));
                return action;
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
