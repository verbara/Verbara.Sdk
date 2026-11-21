using System.Collections.Concurrent;
using System.Diagnostics;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// The SDK's own <see cref="VoiceAiSessionBroker"/> next to a host's side subscriber of
/// <see cref="AudioSocketServer.OnSessionStarted"/> on one server, the shape the broker's remarks and the
/// voice-session ending guide invite: whatever the side subscriber does wrong, and in whichever order the two
/// subscribed, the broker's handler is entered and reads the whole call.
/// </summary>
/// <remarks>
/// Real server, real broker, real <see cref="AudioSocketClient"/> on <c>127.0.0.1</c> port 0 (ADR-0044). Ordered by
/// construction, never by a delay: the audio is sent only once the server has decided what to do with the connection,
/// read as the end of its <c>audiosocket.session</c> activity, which the server stops only after it has awaited every
/// subscriber and, when it closes, after the close. Up to 2.10.x a side subscriber that threw synchronously before the
/// broker kept the broker from running, and one that failed after the broker had subscribed cut the broker's call.
/// </remarks>
public sealed class VoiceAiSessionBrokerSideSubscriberFaultTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const int CallFrames = 10;

    [Theory]
    [InlineData(SideOrder.BeforeTheBroker, SideFailure.Throws)]
    [InlineData(SideOrder.BeforeTheBroker, SideFailure.FaultsLater)]
    [InlineData(SideOrder.AfterTheBroker, SideFailure.Throws)]
    [InlineData(SideOrder.AfterTheBroker, SideFailure.FaultsLater)]
    public async Task OnSessionStarted_ShouldLetTheBrokerServeTheWholeCall_WhenASideSubscriberFails(
        SideOrder order, SideFailure failure)
    {
        // Arrange
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);
        var handler = new CountingHandler();
        using var broker = new VoiceAiSessionBroker(server, handler, NullLogger<VoiceAiSessionBroker>.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<AudioSocketSession, ValueTask> side = failure == SideFailure.Throws
            ? _ => throw new InvalidOperationException("the side subscriber's own failure")
            : async _ =>
            {
                await gate.Task;
                throw new InvalidOperationException("the side subscriber's own failure");
            };
        using var decisions = new DecisionListener();
        await server.StartAsync(CancellationToken.None);
        if (order == SideOrder.BeforeTheBroker)
            server.OnSessionStarted += side;
        await broker.StartAsync(CancellationToken.None);
        if (order == SideOrder.AfterTheBroker)
            server.OnSessionStarted += side;

        // Act — the side subscriber fails only once the broker's handler was entered (the broker enters it inline);
        // the call's audio is sent only once the server has decided.
        var channelId = Guid.NewGuid();
        var decided = decisions.Decided(channelId);
        await using var caller = new QuietClient(await AudioSocketPeer.ConnectAsync(server, channelId));
        await Task.WhenAny(handler.Entered.Task, decided).WaitAsync(SignalTimeout);
        var enteredBeforeTheDecision = handler.Entered.Task.IsCompleted;
        gate.TrySetResult();
        await decided.WaitAsync(SignalTimeout);
        var sendFault = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < CallFrames; i++)
                await caller.Client.SendAudioAsync(new byte[320]);
        });
        var read = await Record.ExceptionAsync(() => handler.Done.Task.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            enteredBeforeTheDecision.Should().BeTrue("the broker's handler is entered whatever the side subscriber did");
            sendFault.Should().BeNull("the connection the broker serves stays open after the side subscriber failed");
            read.Should().BeNull("the broker's handler reads until the call's last frame");
            Volatile.Read(ref handler.Frames).Should().Be(CallFrames, "the broker's handler reads every frame of the call");
        }

        await broker.StopAsync(CancellationToken.None);
    }

    public enum SideOrder { BeforeTheBroker, AfterTheBroker }

    /// <summary>
    /// The caller's client, whose disposal swallows the transport error of a connection the server already cut, so the
    /// test reports what its assertions found rather than the broken pipe of the client's last flush.
    /// </summary>
    private sealed class QuietClient(AudioSocketClient client) : IAsyncDisposable
    {
        public AudioSocketClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
            }
            catch (IOException)
            {
                // The server closed the connection first; nothing is left to release.
            }
        }
    }

    public enum SideFailure { Throws, FaultsLater }

    /// <summary>The broker's handler: reads the call until its last frame and says how many it read.</summary>
    private sealed class CountingHandler : ISessionHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly TaskCompletionSource<int> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Frames;

        public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct)
        {
            Entered.TrySetResult();
            try
            {
                await foreach (var _ in session.ReadAudioAsync(ct))
                {
                    if (Interlocked.Increment(ref Frames) >= CallFrames)
                        break;
                }
            }
            finally
            {
                Done.TrySetResult(Volatile.Read(ref Frames));
            }
        }
    }

    /// <summary>
    /// Signals, per channel, the end of the server's <c>audiosocket.session</c> activity: the server has awaited every
    /// subscriber of that connection and acted. Activity listeners are process-wide, so each signal is keyed by the
    /// channel id the activity carries.
    /// </summary>
    private sealed class DecisionListener : IDisposable
    {
        private const string AudioSocketSourceName = "Verbara.Sdk.VoiceAi.AudioSocket";

        private readonly ConcurrentDictionary<string, TaskCompletionSource> _decided = new();
        private readonly ActivityListener _listener;

        public DecisionListener()
        {
            _listener = new ActivityListener
            {
                // By name, not by reference: the source announces itself to the listeners from inside its own
                // static initialiser, where AudioSocketActivitySource.Source still reads null, and a listener
                // that answers "no" there is never asked again.
                ShouldListenTo = source => source.Name == AudioSocketSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem("asterisk.channel.id") is string id)
                        Signal(id).TrySetResult();
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Task Decided(Guid channelId) => Signal(channelId.ToString()).Task;

        public void Dispose() => _listener.Dispose();

        private TaskCompletionSource Signal(string channelId) =>
            _decided.GetOrAdd(channelId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
