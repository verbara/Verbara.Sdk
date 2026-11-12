using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A <see cref="ManagerAction"/> the SDK does not declare used to be written under its class name with every
/// <c>Action</c> removed and none of its fields. Each of the three send paths now refuses it before any byte is
/// written, names its type and the raw action, and leaves the connection usable.
/// </summary>
public sealed class AmiConnectionUnwritableActionTests : IAsyncLifetime
{
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly Pipe _serverToClient = new();
    private readonly Pipe _clientToServer = new();
    private readonly AmiConnection _sut;

    public AmiConnectionUnwritableActionTests()
    {
        _sut = CreateConnection(_serverToClient, _clientToServer);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _serverToClient.Writer.CompleteAsync();
        await _clientToServer.Reader.CompleteAsync();
        await _sut.DisposeAsync();
    }

    /// <summary>A consumer's own action with a property the SDK never wrote.</summary>
    private sealed class ConsumerQueueProbeAction : ManagerAction
    {
        public string? Queue { get; set; }
    }

    /// <summary>A consumer's own action whose extra fields the SDK never wrote.</summary>
    private sealed class ConsumerExtraFieldsAction : ManagerAction, IHasExtraFields
    {
        public IEnumerable<KeyValuePair<string, string>> GetExtraFields() => [new("Channel", "PJSIP/2000")];
    }

    /// <summary>A field-less action named like a real Asterisk action, which worked by accident (ruling F3).</summary>
    private sealed class LoggerRotateAction : ManagerAction;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in new[] { "property", "extra fields", "named like its action" })
        {
            foreach (var path in new[] { "SendActionAsync", "SendActionAsync<TResponse>", "SendEventGeneratingActionAsync" })
                data.Add(kind, path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Send_ShouldThrowArgumentExceptionNamingTypeAndRawActionAndStayUsable_WhenActionIsNotOneTheSdkCanWrite(
        string kind, string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ConnectWithSuccessfulLoginAsync(cts.Token);
        ManagerAction action = kind switch
        {
            "property" => new ConsumerQueueProbeAction { Queue = "support" },
            "extra fields" => new ConsumerExtraFieldsAction(),
            "named like its action" => new LoggerRotateAction(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind."),
        };

        Func<Task> act = path switch
        {
            "SendActionAsync" => async () => await _sut.SendActionAsync(action, cts.Token),
            "SendActionAsync<TResponse>" => async () => await _sut.SendActionAsync<ManagerResponse>(action, cts.Token),
            "SendEventGeneratingActionAsync" => async () =>
            {
                await foreach (var evt in _sut.SendEventGeneratingActionAsync(action, cts.Token))
                    Assert.Fail($"A refused action is never sent, so no {evt.EventType} event can answer it.");
            },
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown path."),
        };

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;

        thrown.Message.Should().Contain(action.GetType().FullName!).And.Contain("RawAction");
        thrown.ParamName.Should().Be("action");
        await ShouldLeaveNothingBehindAndStayUsableAsync(cts.Token);
    }

    private async Task ShouldLeaveNothingBehindAndStayUsableAsync(CancellationToken ct)
    {
        _clientToServer.Writer.UnflushedBytes.Should().Be(0, "no byte of the refused action may reach the transport");
        _clientToServer.Reader.TryRead(out _).Should().BeFalse("no byte of the refused action may reach the transport");

        // A held write lock or a stale registration would stall or misroute this ping; the line-break tests pin
        // the internals of the same refusal path directly.
        var server = Task.Run(async () =>
        {
            var text = await ReadActionAsync(_clientToServer.Reader, ct);
            await WriteResponseAsync(_serverToClient.Writer, "Success", ExtractActionId(text), [new("Ping", "Pong")]);
            return text;
        }, ct);

        var response = await _sut.SendActionAsync(new PingAction(), ct);
        var sent = await server;

        response.Response.Should().Be("Success");
        sent.Should().MatchRegex(@"^Action: Ping\r\nActionID: [^\r\n]+\r\n\r\n$", "the ping is all that reaches the wire");
    }

    private async Task ConnectWithSuccessfulLoginAsync(CancellationToken ct)
    {
        var login = SimulateSuccessfulLoginAsync(ct);
        await _sut.ConnectAsync(ct);
        await login;
    }

    private static AmiConnection CreateConnection(Pipe serverToClient, Pipe clientToServer)
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
            Username = "admin",
            Password = "secret",
            AutoReconnect = false,
            EnableHeartbeat = false,
            DefaultResponseTimeout = TimeSpan.FromSeconds(2),
            DefaultEventTimeout = TimeSpan.FromSeconds(2),
        });

        return new AmiConnection(options, socketFactory, NullLogger<AmiConnection>.Instance);
    }

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
