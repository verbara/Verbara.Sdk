using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The queue an <c>AgentComplete</c> names reaches the session manager: <see cref="VerbaraServer"/>'s switch raises
/// <see cref="AgentManager.QueueCallerCompleted"/> carrying the frame's <c>Queue</c> header, and <see langword="null"/>
/// when the frame carries none.
/// </summary>
/// <remarks>
/// The frame is copied from a capture taken on Asterisk 22.9.0 (<c>queue-shapes-asterisk-22.9.0.raw</c> in the
/// session functional tests' recordings): every header, in Asterisk's order, as the connection hands it over in
/// <see cref="ManagerEvent.RawFields"/>. The frame with no <c>Queue</c> line is synthetic; none of the recorded
/// <c>AgentComplete</c> frames lacks it.
/// </remarks>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerbaraServerQueueCallerCompletedTests : IAsyncLifetime
{
    /// <summary>An <c>AgentComplete</c> as Asterisk 22.9.0 sent it, verbatim.</summary>
    private const string RecordedFrame = """
        Event: AgentComplete
        Privilege: agent,all
        Channel: PJSIP/pstn-00000002
        ChannelState: 6
        ChannelStateDesc: Up
        CallerIDNum: 5550001
        CallerIDName: L
        ConnectedLineNum: <unknown>
        ConnectedLineName: <unknown>
        Language: en
        AccountCode:
        Context: from-pstn
        Exten: 4001
        Priority: 1
        Uniqueid: 1790519558.2
        Linkedid: 1790519558.2
        DestChannel: Local/agent@agents-00000000;1
        DestChannelState: 6
        DestChannelStateDesc: Up
        DestCallerIDNum: 4001
        DestCallerIDName: <unknown>
        DestConnectedLineNum: 5550001
        DestConnectedLineName: L
        DestLanguage: en
        DestAccountCode:
        DestContext: agents
        DestExten: 4001
        DestPriority: 1
        DestUniqueid: 1790519558.3
        DestLinkedid: 1790519558.2
        Queue: q-local
        Interface: Local/agent@agents/n
        MemberName: Local/agent@agents/n
        HoldTime: 2
        TalkTime: 6
        Reason: caller
        """;

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection;
    private readonly VerbaraServer _sut;
    private IObserver<ManagerEvent>? _observer;

    public VerbaraServerQueueCallerCompletedTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs))
            .Returns(Substitute.For<IDisposable>());
        _connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable());
        _sut = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _sut.DisposeAsync().AsTask().WaitAsync(CleanupBound);

    [Fact]
    public async Task EventObserver_ShouldRaiseQueueCallerCompletedWithTheFramesQueue_WhenARecordedAgentCompleteArrives()
    {
        var raised = await DeliverAsync(Frame(RecordedFrame));

        raised.Select(Describe).Should().Equal(
            ["caller=1790519558.2 linkedid=1790519558.2 member=Local/agent@agents/n talk=6 queue=q-local"],
            "the recorded AgentComplete names queue q-local: the internal event carries it beside the caller, the "
            + "call, the member and the talk time it already carried");
    }

    [Fact]
    public async Task EventObserver_ShouldRaiseQueueCallerCompletedWithNoQueue_WhenTheAgentCompleteCarriesNoQueueLine_Synthetic()
    {
        var withoutQueue = string.Join('\n', RecordedFrame.Split('\n').Where(l => !l.StartsWith("Queue:", StringComparison.Ordinal)));

        var raised = await DeliverAsync(Frame(withoutQueue));

        raised.Select(Describe).Should().Equal(
            ["caller=1790519558.2 linkedid=1790519558.2 member=Local/agent@agents/n talk=6 queue=(null)"],
            "with the Queue line removed the internal event still carries the caller, the call, the member and the "
            + "talk time, and no queue");
    }

    private async Task<List<QueueCallerCompletion>> DeliverAsync(AgentCompleteEvent frame)
    {
        await _sut.StartAsync();
        _observer.Should().NotBeNull("StartAsync must capture the observer");
        var raised = new List<QueueCallerCompletion>();
        _sut.Agents.QueueCallerCompleted += raised.Add;
        _observer!.OnNext(frame);
        return raised;
    }

    /// <summary>
    /// The completion as the case compares it; a completion with no queue reads as <c>(null)</c>.
    /// </summary>
    private static string Describe(QueueCallerCompletion completion) =>
        string.Create(CultureInfo.InvariantCulture,
            $"caller={completion.CallerUniqueId} linkedid={completion.LinkedId} member={completion.MemberInterface} "
            + $"talk={completion.TalkTimeSeconds} queue={completion.Queue ?? "(null)"}");

    /// <summary>
    /// The typed <see cref="AgentCompleteEvent"/> the connection would hand over for <paramref name="text"/>: every
    /// header in <see cref="ManagerEvent.RawFields"/>, and the typed members the switch reads set from the same lines.
    /// </summary>
    private static AgentCompleteEvent Frame(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                fields[line[..colon]] = line[(colon + 1)..].Trim();
        }

        return new AgentCompleteEvent
        {
            EventType = fields["Event"],
            Channel = fields["Channel"],
            UniqueId = fields["Uniqueid"],
            LinkedId = fields["Linkedid"],
            DestChannel = fields["DestChannel"],
            DestUniqueId = fields["DestUniqueid"],
            DestLinkedId = fields["DestLinkedid"],
            Interface = fields["Interface"],
            HoldTime = long.Parse(fields["HoldTime"], CultureInfo.InvariantCulture),
            TalkTime = long.Parse(fields["TalkTime"], CultureInfo.InvariantCulture),
            Reason = fields["Reason"],
            RawFields = fields,
        };
    }

    private static async IAsyncEnumerable<ManagerEvent> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }
}
