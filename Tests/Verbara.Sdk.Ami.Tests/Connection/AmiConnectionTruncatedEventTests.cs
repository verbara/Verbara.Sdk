using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A peer that closes the stream in the middle of an event delivers no event for it: the handler receives every event
/// the peer completed, and nothing built from the fields read before the close. No wall clock is read except
/// <see cref="Bound"/>, a hang bound every wait ends long before.
/// </summary>
public sealed class AmiConnectionTruncatedEventTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReaderLoop_ShouldDispatchNoEvent_WhenThePeerClosesTheStreamInTheMiddleOfAnEvent()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var factory = new PipedSocketFactory();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
        }), factory, NullLogger<AmiConnection>.Instance);
        var dispatched = new List<(string? Type, string? Channel)>();
        connection.OnEvent += evt =>
        {
            lock (dispatched)
                dispatched.Add((evt.EventType, evt.RawFields?.GetValueOrDefault("Channel")));
            return ValueTask.CompletedTask;
        };
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

        (await socket.WriteEventAsync("Newstate", [new("Channel", "PJSIP/whole-1"), new("Uniqueid", "1.1")])).Should().BeTrue();
        (await socket.WriteAsync("Event: Hangup\r\nChannel: PJSIP/cut-1\r\n")).Should().BeTrue();
        socket.CloseFromPeer();
        await disconnected.Task.WaitAsync(Bound);

        using (new AssertionScope())
        {
            lock (dispatched)
            {
                dispatched.Should().Equal([("Newstate", "PJSIP/whole-1")],
                    "the Hangup was cut by the close: its fields are a fragment, and no handler receives it");
            }
        }
    }
}
