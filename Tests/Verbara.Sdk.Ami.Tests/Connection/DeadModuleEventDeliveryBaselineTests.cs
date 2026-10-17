using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Events;
using FluentAssertions;
using FluentAssertions.Execution;
using static Verbara.Sdk.Ami.Tests.Connection.AmiDispatchTestKit;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The event names that dead Asterisk modules sent, or that Asterisk 10 or 12 renamed or dropped, still reach every subscriber
/// as their typed class: a deprecation mark changes what the compiler reports, never what the connection delivers.
/// </summary>
/// <remarks>
/// Each name is written as Asterisk 11 or earlier spelled it, through a real connection over the in-memory transport,
/// followed by the sentinel; every wait ends on its signal, bounded by <see cref="Bound"/>.
/// </remarks>
public sealed class DeadModuleEventDeliveryBaselineTests
{
#pragma warning disable VSDK0005 // pins the deprecated surface until its removal in 2.10.0
    public static TheoryData<string, Type> MarkedEventNames => new()
    {
        { "Agentcallbacklogin", typeof(AgentCallbackLoginEvent) },
        { "Agentcallbacklogoff", typeof(AgentCallbackLogoffEvent) },
        { "QueueMemberPaused", typeof(QueueMemberPausedEvent) },
        { "MusicOnHold", typeof(MusicOnHoldEvent) },
        { "FaxReceived", typeof(FaxReceivedEvent) },
        { "DTMF", typeof(DtmfEvent) },
        { "Transfer", typeof(TransferEvent) },
        { "ChannelReload", typeof(ChannelReloadEvent) },
        { "ChannelUpdate", typeof(ChannelUpdateEvent) },
        { "JabberEvent", typeof(JabberEventEvent) },
        { "AGIExec", typeof(AgiExecEvent) },
        { "AsyncAGI", typeof(AsyncAgiEvent) },
        { "SendFAXStatus", typeof(SendFaxStatusEvent) },
        { "BridgeExec", typeof(BridgeExecEvent) },
        { "ModuleLoadReport", typeof(ModuleLoadReportEvent) },
    };
#pragma warning restore VSDK0005

    [Theory]
    [MemberData(nameof(MarkedEventNames))]
    public async Task Subscribers_ShouldReceiveTheTypedClass_WhenAMarkedEventNameArrives(string wireName, Type expected)
    {
        var received = await DeliverAsync(wireName, [new("Channel", "SIP/100-00000001")]);

        using (new AssertionScope())
        {
            received.Should().ContainSingle("the one event written before the sentinel reaches the subscriber")
                .Which.Should().BeOfType(expected, "{0} maps to its typed class, mark or no mark", wireName);
            received.Single().EventType.Should().BeEquivalentTo(wireName, "the event keeps the name Asterisk sent");
        }
    }

#pragma warning disable VSDK0005 // pins the deprecated surface until its removal in 2.10.0
    [Fact]
    public async Task Subscribers_ShouldReceiveTheQueuePauseFields_WhenQueueMemberPausedArrives()
    {
        // What Live reads from it: queue, member location, paused flag and reason, as Asterisk 11 sent them.
        var received = await DeliverAsync("QueueMemberPaused",
            [new("Queue", "support"), new("Location", "SIP/2000"), new("MemberName", "Agent 2000"), new("Paused", "1"), new("Reason", "lunch")]);

        var paused = received.Should().ContainSingle().Which.Should().BeOfType<QueueMemberPausedEvent>().Subject;
        using (new AssertionScope())
        {
            paused.Queue.Should().Be("support");
            paused.Location.Should().Be("SIP/2000");
            paused.MemberName.Should().Be("Agent 2000");
            paused.Paused.Should().BeTrue("Asterisk 11 sends Paused: 1 for a pause");
            paused.Reason.Should().Be("lunch");
        }
    }
#pragma warning restore VSDK0005

    private static async Task<IReadOnlyList<ManagerEvent>> DeliverAsync(string wireName, KeyValuePair<string, string>[] headers)
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, new DispatchLogger());
        var subscriber = new RecordingSubscriber();
        using var subscription = connection.Subscribe(subscriber);
        var socket = await ConnectAsync(connection, factory, peer);

        (await socket.WriteEventAsync(wireName, headers)).Should().BeTrue();
        await WriteSentinelAsync(socket);
        (await CompletesWithinBoundAsync(subscriber.SentinelReached)).Should().BeTrue(
            "the subscriber receives every event up to the sentinel written last");
        return subscriber.Received;
    }

    private sealed class RecordingSubscriber : IObserver<ManagerEvent>
    {
        private readonly ConcurrentQueue<ManagerEvent> _received = new();
        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ManagerEvent> Received => [.. _received];

        public Task SentinelReached => _sentinel.Task;

        public void OnNext(ManagerEvent value)
        {
            if (IsSentinel(value))
                _sentinel.TrySetResult();
            else
                _received.Enqueue(value);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
