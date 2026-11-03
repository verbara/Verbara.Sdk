using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The live channel table follows the dialplan location (<c>Newexten</c>) and the caller identity (<c>NewCallerid</c>)
/// Asterisk reports, held by the four <c>dialplan-and-parking-*</c> captures replayed through the production reader,
/// deserializer and the observer the server subscribed, with a real session engine attached. Nothing here calls the
/// table directly and no frame is composed; the table and the session are read between two frames.
/// </summary>
/// <remarks>
/// The capture's first call is a Local pair, <c>Local/callerid@capture-00000000;1</c> and <c>;2</c>. Both open reading
/// <c>&lt;unknown&gt;</c>; a <c>NewCallerid</c> sets each to <c>7100</c>/<c>Capture</c>; then <c>;2</c> runs
/// <c>Set(CALLERID(num))</c> at priority 3 and <c>Set(CALLERID(name))</c> at priority 4, each reported as a
/// <c>NewCallerid</c> carrying that priority, and jumps to <c>capture-hop,hop</c>. The second call,
/// <c>Local/hold@capture-00000001;2</c>, is moved by an AMI <c>Redirect</c> to extension <c>redirected</c>.
/// </remarks>
public sealed class ChannelFollowsDialplanReplayTests
{
    private const string Leg1 = "Local/callerid@capture-00000000;1";
    private const string Leg2 = "Local/callerid@capture-00000000;2";
    private const string Redirected = "Local/hold@capture-00000001;2";

    public static TheoryData<string> Captures => new()
    {
        "dialplan-and-parking-asterisk-20.20.1.raw",
        "dialplan-and-parking-asterisk-22.11.0.raw",
        "dialplan-and-parking-asterisk-23.5.0.raw",
        "dialplan-and-parking-asterisk-24.0.0-rc2.raw",
    };

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task NewCallerId_ShouldReachTheHeldChannel_WhenTheCallerIdIsSetInTheDialplan(string fixture)
    {
        Location? before = null;
        Location? after = null;
        (string?, string?) identityBefore = default;
        (string?, string?) identityAfter = default;

        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            beforeEach: (evt, probe) =>
            {
                if (IsFirstCallerIdOf(evt, Leg1, after) && probe.Server.Channels.GetByName(Leg1) is { } channel)
                {
                    before = Location.Of(channel);
                    identityBefore = (channel.CallerIdNum, channel.CallerIdName);
                }
            },
            afterEach: (evt, probe) =>
            {
                if (IsFirstCallerIdOf(evt, Leg1, after) && probe.Server.Channels.GetByName(Leg1) is { } channel)
                {
                    after = Location.Of(channel);
                    identityAfter = (channel.CallerIdNum, channel.CallerIdName);
                }
            });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty();
        identityBefore.Should().Be(("<unknown>", "<unknown>"), "the leg opens with no caller id");
        identityAfter.Should().Be(("7100", "Capture"), "the NewCallerid for the held channel moves its number and name");
        after.Should().Be(before, "context, extension, priority and history do not move on a NewCallerid");
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task NewCallerId_ShouldMoveNothingButTheIdentity_WhenTheFrameReportsAnotherPriority(string fixture)
    {
        var observed = new List<(string Priority, Location Before, Location After, string? Num, string? Name, string? FrameNum, string? FrameName)>();
        Location? pending = null;

        // Newexten is withheld, as on a connection that does not read the dialplan class: the leg stays held at
        // priority 1, so a handler that wrote the frame's priority (3 or 4) would move it.
        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            drop: static evt => evt is NewExtenEvent,
            beforeEach: (evt, probe) =>
            {
                if (IsCallerIdOf(evt, Leg2) && probe.Server.Channels.GetByName(Leg2) is { } channel)
                    pending = Location.Of(channel);
            },
            afterEach: (evt, probe) =>
            {
                if (IsCallerIdOf(evt, Leg2) && pending is { } before && probe.Server.Channels.GetByName(Leg2) is { } channel)
                {
                    var frame = (NewCallerIdEvent)evt;
                    observed.Add((frame.Priority?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", before,
                        Location.Of(channel), channel.CallerIdNum, channel.CallerIdName, frame.CallerIdNum, frame.CallerIdName));
                    pending = null;
                }
            });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty();
        observed.Select(static o => o.Priority).Should().Equal(["1", "3", "4"], "the leg's NewCallerid frames report priorities 1, 3 and 4");
        foreach (var o in observed)
        {
            o.Before.Priority.Should().Be(1, "with Newexten withheld the leg stays at its admission priority");
            o.After.Should().Be(o.Before, "the frame at priority {0} moves no location member and no history", o.Priority);
            o.Num.Should().Be(o.FrameNum);
            o.Name.Should().Be(o.FrameName);
        }
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task NewCallerId_ShouldLeaveTheHistoryCount_WhenNewextenIsDelivered(string fixture)
    {
        var counts = new List<(int Before, int After)>();
        var before = -1;

        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            beforeEach: (evt, probe) =>
            {
                if (IsCallerIdOf(evt, Leg2) && probe.Server.Channels.GetByName(Leg2) is { } channel)
                    before = channel.ExtensionHistory.Count;
            },
            afterEach: (evt, probe) =>
            {
                if (IsCallerIdOf(evt, Leg2) && probe.Server.Channels.GetByName(Leg2) is { } channel)
                    counts.Add((before, channel.ExtensionHistory.Count));
            });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty();
        counts.Should().HaveCount(3);
        counts.Should().AllSatisfy(c => c.After.Should().Be(c.Before, "a NewCallerid records no visit"));
        counts.Select(static c => c.Before).Should().Equal([0, 3, 4], "the Newexten frames before each NewCallerid were recorded");
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task NewCallerId_ShouldLeaveTheParticipantsIdentity_WhenItChangesTheChannel(string fixture)
    {
        string? channelNum = null;
        string? participantNum = null;

        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            afterEach: (evt, probe) =>
            {
                if (!IsCallerIdOf(evt, Leg1) || channelNum is not null || probe.Server.Channels.GetByName(Leg1) is not { } channel)
                    return;

                channelNum = channel.CallerIdNum;
                participantNum = probe.Manager.GetByChannelId(channel.UniqueId)?.Participants
                    .Single(p => string.Equals(p.UniqueId, channel.UniqueId, StringComparison.Ordinal)).CallerIdNum;
            });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty();
        channelNum.Should().Be("7100", "the live channel takes the reported number");
        participantNum.Should().Be("<unknown>", "the participant keeps the identity it was created with");
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task NewCallerId_ShouldBeIgnored_WhenItArrivesAfterItsChannelsHangup(string fixture)
    {
        var hungUp = new HashSet<string>(StringComparer.Ordinal);
        var late = new List<(string Channel, bool Held, int AddedDuring)>();
        var added = 0;
        var addedBefore = 0;
        var subscribed = false;

        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            beforeEach: (evt, probe) =>
            {
                if (!subscribed)
                {
                    probe.Server.Channels.ChannelAdded += _ => added++;
                    subscribed = true;
                }

                addedBefore = added;
            },
            afterEach: (evt, probe) =>
            {
                if (evt is HangupEvent { UniqueId: { } gone })
                    hungUp.Add(gone);
                if (evt is NewCallerIdEvent { UniqueId: { } id } frame && hungUp.Contains(id)
                    && !(frame.RawFields?.ContainsKey("ActionID") ?? false))
                    late.Add((frame.Channel ?? "", probe.Server.Channels.GetByUniqueId(id) is not null, added - addedBefore));
            });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty();
        late.Should().HaveCount(2, "each capture holds two NewCallerid frames that follow their channel's Hangup");
        late.Should().AllSatisfy(l =>
        {
            l.Held.Should().BeFalse("{0} is not admitted by a frame after its hangup", l.Channel);
            l.AddedDuring.Should().Be(0, "nothing is raised");
        });
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task Newexten_ShouldMoveTheRedirectedChannel_WhenAsteriskReportsWhereItWent(string fixture)
    {
        AsteriskChannel? first = null;
        AsteriskChannel? redirected = null;
        Location? location = null;
        var sameInBothIndices = false;

        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            afterEach: (evt, probe) =>
            {
                if (evt is NewChannelEvent { Channel: Redirected })
                    first = probe.Server.Channels.GetByName(Redirected);
                if (evt is NewExtenEvent { Channel: Redirected, Exten: "redirected", Priority: 1 } frame)
                {
                    redirected = probe.Server.Channels.GetByUniqueId(frame.UniqueId!);
                    location = redirected is null ? null : Location.Of(redirected);
                    sameInBothIndices = redirected is not null
                        && ReferenceEquals(probe.Server.Channels.GetByName(Redirected), redirected);
                }
            });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty();
        first.Should().NotBeNull();
        first!.Extension.Should().Be("redirected", "the instance held since admission now reads where the redirect sent it");
        redirected.Should().BeSameAs(first, "it is the same instance");
        sameInBothIndices.Should().BeTrue("it is held in both indices");
        location!.Value.Context.Should().Be("capture");
        location.Value.Extension.Should().Be("redirected");
        location.Value.Priority.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task Newexten_ShouldRecordEachReportedStepInOrder_WhenTheCapturedFramesAreReplayed(string fixture)
    {
        var expected = new List<(string, string, int)>();
        IReadOnlyList<ExtensionHistoryEntry>? history = null;
        Location? last = null;

        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            beforeEach: (evt, probe) =>
            {
                // Read just before the leg's hangup removes it from the table.
                if (evt is HangupEvent { Channel: Leg2 } && probe.Server.Channels.GetByName(Leg2) is { } channel)
                {
                    history = channel.ExtensionHistory;
                    last = Location.Of(channel);
                }
            },
            afterEach: (evt, _) =>
            {
                if (evt is NewExtenEvent { Channel: Leg2 } frame)
                    expected.Add((frame.Context!, frame.Exten!, frame.Priority!.Value));
            });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty();
        expected.Should().HaveCount(8, "the leg runs five steps of callerid and three of capture-hop,hop");
        history.Should().NotBeNull();
        history!.Select(static h => (h.Context, h.Extension, h.Priority)).Should().Equal(expected);
        last!.Value.Should().Be(new Location("capture-hop", "hop", 3, expected.Count));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task HeldChannel_ShouldKeepItsAdmittedLocation_WhenTheConnectionReceivesNoNewexten(string fixture)
    {
        Location? last = null;

        var swallowed = await AmiCaptureReplay.ReplayFrameByFrameAsync(fixture,
            drop: static evt => evt is NewExtenEvent,
            beforeEach: (evt, probe) =>
            {
                if (evt is HangupEvent { Channel: Leg2 } && probe.Server.Channels.GetByName(Leg2) is { } channel)
                    last = Location.Of(channel);
            },
            afterEach: static (_, _) => { });

        using var scope = new AssertionScope();
        swallowed.Should().BeEmpty("nothing fails without Newexten");
        last.Should().Be(new Location("capture", "callerid", 1, 0), "the leg keeps the location it was admitted with");
    }

    /// <summary>
    /// A session opens at the location of its <c>Newchannel</c>, whatever <c>Newexten</c> follows. The frames are
    /// composed and make no claim about what Asterisk sends; the claim is about the SDK's delivery order: the session
    /// engine's admission handler runs before the next frame is dispatched.
    /// </summary>
    [Fact]
    public async Task Session_ShouldKeepTheLocationOfItsCreation_WhenNewextenFollowsNewchannel()
    {
        await using var moved = await QueueCallRig.StartAsync();
        await using var control = await QueueCallRig.StartAsync();
        var newChannel = new NewChannelEvent
        {
            UniqueId = "1700000000.1", Channel = "PJSIP/trunk-00000001", ChannelState = "4", Context = "from-pstn",
            Exten = "5551000", Priority = 1, Linkedid = "1700000000.1", CallerIdNum = "5550100",
        };

        moved.Deliver([
            newChannel,
            new NewExtenEvent
            {
                UniqueId = "1700000000.1", Channel = "PJSIP/trunk-00000001", Context = "internal", Exten = "200",
                Extension = "200", Priority = 1, Application = "Dial",
            },
        ]);
        control.Deliver([newChannel]);

        var session = moved.Manager.GetByChannelId("1700000000.1");
        var reference = control.Manager.GetByChannelId("1700000000.1");
        using var scope = new AssertionScope();
        moved.Server.Channels.GetByUniqueId("1700000000.1")!.Extension.Should().Be("200", "the live channel followed the Newexten");
        session.Should().NotBeNull();
        session!.Context.Should().Be("from-pstn");
        session.Extension.Should().Be("5551000");
        session.Direction.Should().Be(reference!.Direction, "the direction is inferred from the Newchannel's location");
    }

    private static bool IsCallerIdOf(ManagerEvent evt, string channel) =>
        evt is NewCallerIdEvent frame && string.Equals(frame.Channel, channel, StringComparison.Ordinal)
        && !(frame.RawFields?.ContainsKey("ActionID") ?? false);

    private static bool IsFirstCallerIdOf(ManagerEvent evt, string channel, Location? already) =>
        already is null && IsCallerIdOf(evt, channel);

    /// <summary>What a channel says about where it is: context, extension, priority and the length of its history.</summary>
    private readonly record struct Location(string? Context, string? Extension, int Priority, int HistoryCount)
    {
        public static Location Of(AsteriskChannel channel) =>
            new(channel.Context, channel.Extension, channel.Priority, channel.ExtensionHistory.Count);
    }
}
