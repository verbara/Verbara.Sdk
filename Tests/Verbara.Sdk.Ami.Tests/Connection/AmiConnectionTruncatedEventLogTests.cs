using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A message the peer's close cut is logged once at <c>Debug</c> with what it was, when the reader loop ends on that
/// close; a close between messages logs no such line. The loop ends before the connection reports
/// <see cref="AmiConnectionState.Disconnected"/>, so that state is the fence. No wall clock is read except
/// <see cref="Bound"/>, a hang bound every wait ends long before.
/// </summary>
public sealed class AmiConnectionTruncatedEventLogTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string DiscardedFormat = "[AMI] Discarded a message cut by the end of the stream: type={Type} fields={FieldCount}";

    [Fact]
    public async Task ReaderLoop_ShouldLogTheDiscardedFragmentAtDebug_WhenThePeerClosesTheStreamInTheMiddleOfAnEvent()
    {
        var lines = await CloseAfterAsync("Event: Hangup\r\nChannel: PJSIP/cut-1\r\n");

        using (new AssertionScope())
        {
            lines.Should().ContainSingle("one message was cut by the close");
            lines.Should().AllSatisfy(l =>
            {
                l.Level.Should().Be(LogLevel.Debug);
                l.Values.GetValueOrDefault("Type").Should().Be("Hangup");
                l.Values.GetValueOrDefault("FieldCount").Should().Be(2);
            });
        }
    }

    [Fact]
    public async Task ReaderLoop_ShouldLogNoDiscardedFragment_WhenThePeerClosesTheStreamBetweenMessages()
    {
        var lines = await CloseAfterAsync("Event: Hangup\r\nChannel: PJSIP/whole-1\r\n\r\n");

        lines.Should().BeEmpty("every message the peer wrote was complete");
    }

    /// <summary>Logs in, writes <paramref name="text"/>, closes from the peer and returns the discarded-fragment lines.</summary>
    private static async Task<List<LogEntry>> CloseAfterAsync(string text)
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var factory = new PipedSocketFactory();
        var logger = new CapturingLogger<AmiConnection>();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
        }), factory, logger);
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var socket = await loggedIn.WaitAsync(Bound);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += _ =>
        {
            if (connection.State == AmiConnectionState.Disconnected)
                disconnected.TrySetResult();
        };

        (await socket.WriteAsync(text)).Should().BeTrue();
        socket.CloseFromPeer();
        await disconnected.Task.WaitAsync(Bound);

        return logger.Lines(DiscardedFormat);
    }

    private sealed record LogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> Values);

    /// <summary>Every line logged, with its level and its values, kept by its message template.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<(string? Format, LogEntry Entry)> _entries = [];

        public List<LogEntry> Lines(string format)
        {
            lock (_gate)
                return [.. _entries.Where(e => e.Format == format).Select(e => e.Entry)];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    values[pair.Key] = pair.Value;
            }

            var format = values.TryGetValue("{OriginalFormat}", out var f) ? f as string : null;
            lock (_gate)
                _entries.Add((format, new LogEntry(logLevel, values)));
        }
    }
}
