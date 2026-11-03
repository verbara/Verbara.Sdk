using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// The table follows the dialplan location and the caller identity reported for a held channel: what moves, what does
/// not, the history's bound, and what a reload leaves. The values are composed and make no claim about what Asterisk
/// sends; the captured frames are replayed in the session functional suite.
/// </summary>
public sealed class ChannelLocationFollowTests
{
    private readonly ChannelManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnNewExten_ShouldMoveTheLocationAndRecordTheVisit_WhenTheChannelIsHeld()
    {
        _sut.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, context: "test-functional", exten: "100", priority: 1);
        var held = _sut.GetByUniqueId("1.1")!;

        _sut.OnNewExten("1.1", "test-functional", "161", 2);

        using var scope = new AssertionScope();
        held.Context.Should().Be("test-functional");
        held.Extension.Should().Be("161");
        held.Priority.Should().Be(2);
        held.ExtensionHistory.Select(static h => (h.Context, h.Extension, h.Priority)).Should().Equal([("test-functional", "161", 2)]);
        _sut.GetByUniqueId("1.1").Should().BeSameAs(held);
        _sut.GetByName("PJSIP/a-1").Should().BeSameAs(held);
    }

    [Fact]
    public void ExtensionHistory_ShouldKeepTheLast100VisitsInOrder_WhenTheChannelIsReportedAt110Steps()
    {
        _sut.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, context: "ctx", exten: "s", priority: 1);

        for (var step = 1; step <= 110; step++)
            _sut.OnNewExten("1.1", "ctx", "s", step);

        var history = _sut.GetByUniqueId("1.1")!.ExtensionHistory;
        using var scope = new AssertionScope();
        history.Should().HaveCount(100);
        history.Select(static h => h.Priority).Should().Equal(Enumerable.Range(11, 100), "the most recent 100, oldest first");
        _sut.GetByUniqueId("1.1")!.Priority.Should().Be(110);
    }

    [Theory]
    [InlineData(null, "200", 1)]
    [InlineData("", "200", 1)]
    [InlineData("ctx-b", null, 1)]
    [InlineData("ctx-b", "", 1)]
    [InlineData("ctx-b", "200", null)]
    public void OnNewExten_ShouldMoveNothing_WhenTheReportIsMissingAValue(string? context, string? exten, int? priority)
    {
        _sut.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, context: "ctx-a", exten: "100", priority: 1);
        var held = _sut.GetByUniqueId("1.1")!;

        _sut.OnNewExten("1.1", context, exten, priority);

        using var scope = new AssertionScope();
        held.Context.Should().Be("ctx-a");
        held.Extension.Should().Be("100");
        held.Priority.Should().Be(1);
        held.ExtensionHistory.Should().BeEmpty();
    }

    [Fact]
    public void OnNewExtenAndOnNewCallerId_ShouldAdmitNothing_WhenTheTableDoesNotHoldTheChannel()
    {
        var added = 0;
        _sut.ChannelAdded += _ => added++;

        _sut.OnNewExten("missing.1", "ctx", "100", 1);
        _sut.OnNewCallerId("missing.1", "7100", "Capture");

        using var scope = new AssertionScope();
        _sut.ChannelCount.Should().Be(0);
        added.Should().Be(0);
    }

    [Fact]
    public void OnNewExtenAndOnNewCallerId_ShouldRaiseNoChannelEvent_WhenTheChannelMoves()
    {
        _sut.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, context: "ctx", exten: "100", priority: 1);
        var raised = new List<string>();
        _sut.ChannelAdded += _ => raised.Add(nameof(ChannelManager.ChannelAdded));
        _sut.ChannelRemoved += _ => raised.Add(nameof(ChannelManager.ChannelRemoved));
        _sut.ChannelStateChanged += _ => raised.Add(nameof(ChannelManager.ChannelStateChanged));
        _sut.ChannelDialBegin += _ => raised.Add(nameof(ChannelManager.ChannelDialBegin));
        _sut.ChannelDialEnd += _ => raised.Add(nameof(ChannelManager.ChannelDialEnd));
        _sut.ChannelHeld += _ => raised.Add(nameof(ChannelManager.ChannelHeld));
        _sut.ChannelUnheld += _ => raised.Add(nameof(ChannelManager.ChannelUnheld));

        _sut.OnNewExten("1.1", "ctx", "200", 1);
        _sut.OnNewCallerId("1.1", "7100", "Capture");

        raised.Should().BeEmpty("following the dialplan and the identity raises no channel event");
    }

    [Fact]
    public void OnNewCallerId_ShouldMoveOnlyTheNumberAndName_WhenTheChannelIsHeld()
    {
        _sut.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, callerIdNum: "<unknown>", callerIdName: "<unknown>",
            context: "ctx", exten: "100", priority: 3);
        var held = _sut.GetByUniqueId("1.1")!;

        _sut.OnNewCallerId("1.1", "7100", "Capture");

        using var scope = new AssertionScope();
        held.CallerIdNum.Should().Be("7100");
        held.CallerIdName.Should().Be("Capture");
        held.Context.Should().Be("ctx");
        held.Extension.Should().Be("100");
        held.Priority.Should().Be(3);
        held.ExtensionHistory.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileWithSnapshot_ShouldKeepTheFollowedLocationAndHistory_WhenTheSnapshotListsTheEarlierLocation()
    {
        _sut.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, context: "ctx", exten: "100", priority: 1);
        _sut.OnNewExten("1.1", "ctx", "161", 1);
        var held = _sut.GetByUniqueId("1.1")!;

        using (var window = _sut.OpenReadWindow())
            _sut.ReconcileWithSnapshot([new ChannelSnapshotEntry("1.1", "PJSIP/a-1", ChannelState.Up, Context: "ctx", Extension: "100", Priority: 1)], window);

        using var scope = new AssertionScope();
        _sut.GetByUniqueId("1.1").Should().BeSameAs(held, "a reload keeps the held instance");
        held.Extension.Should().Be("161", "an older snapshot never takes a followed channel back");
        held.ExtensionHistory.Should().ContainSingle();
    }

    [Fact]
    public void ExtensionHistory_ShouldReturnACopy_WhenItIsRead()
    {
        _sut.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, context: "ctx", exten: "100", priority: 1);
        _sut.OnNewExten("1.1", "ctx", "200", 1);
        var read = _sut.GetByUniqueId("1.1")!.ExtensionHistory;

        _sut.OnNewExten("1.1", "ctx", "300", 1);

        read.Should().ContainSingle("a list already read does not change under its reader");
    }
}
