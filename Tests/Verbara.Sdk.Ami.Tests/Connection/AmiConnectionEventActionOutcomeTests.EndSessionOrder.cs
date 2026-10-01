using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Channels;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The order in which a session's ending wakes the callers still waiting on it: the reader loop's ending writes the
/// state it chose, and only then abandons the event-generating actions still pending. A caller woken by that abandon
/// (the live state's load among them) reads what comes next, never the <see cref="AmiConnectionState.Connected"/> of
/// the session that just ended.
/// </summary>
/// <remarks>
/// <para>
/// No timing decides the outcome. The probe's collector is given a channel whose continuations run synchronously, so a
/// continuation on its completion runs inside <see cref="ResponseEventCollector.Abandon"/>, on the reader loop, and reads
/// <see cref="AmiConnection.State"/> at that exact point: either the state has been written by then, or it has not.
/// </para>
/// <para>
/// The seam lives only here: two private fields reached by reflection (a test project is not AOT-published, and an
/// <c>extern</c> accessor reads as unmanaged code to the code scan). A renamed field fails the test with
/// <see cref="MissingFieldException"/>; it cannot pass in silence.
/// </para>
/// </remarks>
public sealed partial class AmiConnectionEventActionOutcomeTests
{
    private static ConcurrentDictionary<string, ResponseEventCollector> PendingEventActions(AmiConnection connection) =>
        (ConcurrentDictionary<string, ResponseEventCollector>)PrivateField(typeof(AmiConnection), "_pendingEventActions")
            .GetValue(connection)!;

    private static void ReplaceCollectorChannel(ResponseEventCollector collector, Channel<ManagerEvent> channel) =>
        PrivateField(typeof(ResponseEventCollector), "_channel").SetValue(collector, channel);

    private static Channel<ManagerEvent> CollectorChannel(ResponseEventCollector collector) =>
        (Channel<ManagerEvent>)PrivateField(typeof(ResponseEventCollector), "_channel").GetValue(collector)!;

    private static FieldInfo PrivateField(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)] Type owner, string name) =>
        owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(owner.FullName, name);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EndSession_ShouldAbandonPendingActionsOnlyOnceTheStateIsChosen_WhenTheSessionEnds(bool autoReconnect)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect, LongBackoff);
        var peer = await ConnectAsync(connection, factory, peerCts);

        var probe = new ResponseEventCollector();
        ReplaceCollectorChannel(probe, Channel.CreateUnbounded<ManagerEvent>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = true }));
        var stateAtAbandon = new TaskCompletionSource<AmiConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The channel's completion completes inside Abandon's TryComplete, and runs this continuation there.
        _ = CollectorChannel(probe).Reader.Completion.ContinueWith(
            _ => stateAtAbandon.TrySetResult(connection.State),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        PendingEventActions(connection)["h85-probe"] = probe;

        peer.CloseFromPeer();
        var state = await stateAtAbandon.Task.WaitAsync(Bound);

        using (new AssertionScope())
        {
            probe.SessionEnded.Should().BeTrue("the session's end abandoned the pending action");
            state.Should().NotBe(AmiConnectionState.Connected,
                "the ending writes the state it chose before it abandons the actions still pending");
            state.Should().Be(autoReconnect ? AmiConnectionState.Reconnecting : AmiConnectionState.Disconnecting,
                "a woken caller reads the state the ending chose");
        }
    }
}
