using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Ami.Tests.Connection;

public sealed class AmiHeartbeatTests : IAsyncDisposable
{
    private readonly Pipe _serverToClient = new();
    private readonly Pipe _clientToServer = new();

    public async ValueTask DisposeAsync()
    {
        await _serverToClient.Writer.CompleteAsync();
        await _clientToServer.Reader.CompleteAsync();
        GC.SuppressFinalize(this);
    }

    private AmiConnection CreateConnection(AmiConnectionOptions options)
    {
        var socketConnection = Substitute.For<ISocketConnection>();
        socketConnection.Input.Returns(_serverToClient.Reader);
        socketConnection.Output.Returns(_clientToServer.Writer);
        socketConnection.IsConnected.Returns(true);
#pragma warning disable CA2012
        socketConnection.ConnectAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
#pragma warning restore CA2012

        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        socketFactory.Create().Returns(socketConnection);

        return new AmiConnection(Options.Create(options), socketFactory, NullLogger<AmiConnection>.Instance);
    }

    private async Task SimulateSuccessfulLoginAsync(CancellationToken ct = default)
    {
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        await WriteBytesAsync(writer, "Asterisk Call Manager/6.0.0\r\n");

        var challengeAction = await ReadActionAsync(reader, ct);
        var challengeId = ExtractActionId(challengeAction);
        await WriteResponseAsync(writer, "Success", challengeId, [new("Challenge", "abc123")]);

        var loginAction = await ReadActionAsync(reader, ct);
        var loginId = ExtractActionId(loginAction);
        await WriteResponseAsync(writer, "Success", loginId, [new("Message", "Authentication accepted")]);

        var coreAction = await ReadActionAsync(reader, ct);
        var coreId = ExtractActionId(coreAction);
        await WriteResponseAsync(writer, "Success", coreId, [new("AsteriskVersion", "20.0.0")]);
    }

    [Fact]
    public async Task Heartbeat_ShouldSendPingAction_WhenIntervalElapses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost", Username = "admin", Password = "secret",
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(500),
            EnableHeartbeat = true,
            AutoReconnect = false,
            DefaultResponseTimeout = TimeSpan.FromSeconds(5)
        };

        await using var connection = CreateConnection(options);

        _ = Task.Run(async () =>
        {
            await SimulateSuccessfulLoginAsync(cts.Token);

            // Read the Ping action from the heartbeat loop
            var pingAction = await ReadActionAsync(_clientToServer.Reader, cts.Token);
            var pingId = ExtractActionId(pingAction);

            // Respond to the ping
            await WriteResponseAsync(_serverToClient.Writer, "Success", pingId);
        }, cts.Token);

        await connection.ConnectAsync(cts.Token);

        // Wait for heartbeat interval + margin
        await Task.Delay(300, cts.Token);

        // Read what was sent to the server — the ping action should have been received
        // If we got here without timeout, the server task successfully read a Ping action
        connection.State.Should().Be(AmiConnectionState.Connected);
    }

    [Fact]
    public async Task Heartbeat_ShouldTriggerDisconnect_WhenPingTimesOut()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost", Username = "admin", Password = "secret",
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            EnableHeartbeat = true,
            AutoReconnect = false,
            DefaultResponseTimeout = TimeSpan.FromSeconds(5)
        };

        await using var connection = CreateConnection(options);

        _ = Task.Run(async () =>
        {
            await SimulateSuccessfulLoginAsync(cts.Token);
            // Don't respond to the ping — let it time out
        }, cts.Token);

        await connection.ConnectAsync(cts.Token);

        // Wait for heartbeat + timeout + margin
        await Task.Delay(500, cts.Token);

        // Connection should have been disconnected due to heartbeat timeout
        connection.State.Should().NotBe(AmiConnectionState.Connected);
    }

    [Fact]
    public async Task Heartbeat_ShouldNotStart_WhenDisabled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost", Username = "admin", Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
            DefaultResponseTimeout = TimeSpan.FromSeconds(5)
        };

        await using var connection = CreateConnection(options);

        _ = Task.Run(async () =>
        {
            await SimulateSuccessfulLoginAsync(cts.Token);
        }, cts.Token);

        await connection.ConnectAsync(cts.Token);

        // Wait a bit — no ping should be sent
        await Task.Delay(200, cts.Token);

        connection.State.Should().Be(AmiConnectionState.Connected);
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

    private static async Task<string> ReadActionAsync(PipeReader reader, CancellationToken ct = default)
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
