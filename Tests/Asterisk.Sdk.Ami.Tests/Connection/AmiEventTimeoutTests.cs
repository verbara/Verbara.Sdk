using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Ami.Tests.Connection;

public sealed class AmiEventTimeoutTests : IAsyncDisposable
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
    public async Task SendEventGeneratingAction_ShouldTimeout_WhenNoCompleteEventReceived()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost", Username = "admin", Password = "secret",
            DefaultEventTimeout = TimeSpan.FromSeconds(2),
            AutoReconnect = false,
            EnableHeartbeat = false,
            DefaultResponseTimeout = TimeSpan.FromSeconds(5)
        };

        await using var connection = CreateConnection(options);

        _ = Task.Run(async () =>
        {
            await SimulateSuccessfulLoginAsync(cts.Token);

            // Read the action but never send a Complete event
            var actionText = await ReadActionAsync(_clientToServer.Reader, cts.Token);
            var actionId = ExtractActionId(actionText);

            // Send one event but never the Complete
            await WriteEventAsync(_serverToClient.Writer, "Status", actionId,
                [new("Channel", "PJSIP/2000-001"), new("State", "Up")]);
        }, cts.Token);

        await connection.ConnectAsync(cts.Token);

        var events = new List<Asterisk.Sdk.ManagerEvent>();

        var act = async () =>
        {
            await foreach (var evt in connection.SendEventGeneratingActionAsync(
                new Asterisk.Sdk.Ami.Actions.StatusAction(), cts.Token))
            {
                events.Add(evt);
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        events.Should().HaveCount(1, "one event was sent before timeout");
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

    private static async Task WriteEventAsync(PipeWriter writer, string eventType, string actionId,
        List<KeyValuePair<string, string>>? fields = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Event: {eventType}\r\n");
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
