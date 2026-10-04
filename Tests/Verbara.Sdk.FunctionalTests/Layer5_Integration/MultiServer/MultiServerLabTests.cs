using System.Globalization;
using System.Text.RegularExpressions;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.TestInfrastructure.Stacks;
using FluentAssertions;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>
/// The two-server lab against real Asterisk servers: both servers up on the leg's pinned build, the trunk carrying a
/// call from A to B, each AMI user class receiving what its class allows and nothing it excludes, and a SIPp call
/// entering A and crossing the trunk. These tests prove the lab, not the SDK: the SDK's AMI connection is only the
/// observer.
/// </summary>
/// <remarks>
/// Every count is read after a causal fence — the <c>full</c> user's own count of the calls' <c>Hangup</c> events, then
/// a marker <c>UserEvent</c> each observer must receive — never after a delay. Every zero is asserted next to the
/// <c>full</c> user's positive count of the same event over the same calls.
/// </remarks>
[Trait("Category", "Functional")]
public sealed partial class MultiServerLabTests(MultiServerTestFixture fixture, ITestOutputHelper output)
    : IClassFixture<MultiServerTestFixture>
{
    private readonly MultiServerFixture _lab = fixture.Lab;

    [Fact]
    public async Task BothServers_ShouldBeUpOnThePinnedBuild_WhenTheFixtureIsInitialized()
    {
        WriteVersions();

        foreach (var (server, version) in new[] { ('A', _lab.ServerAVersion), ('B', _lab.ServerBVersion) })
        {
            await using var connection = fixture.Connect(server, MultiServerAmiUsers.Full);
            await connection.ConnectAsync();
            connection.State.Should().Be(AmiConnectionState.Connected, $"server {server}'s full user logs in with the run's secret");
            version.Should().StartWith($"Asterisk {MultiServerTestFixture.PinnedVersion} ",
                $"server {server} runs the leg's pinned base");
        }

        _lab.ServerAAddress.Should().NotBe(_lab.ServerBAddress);
    }

    [Fact]
    public async Task Login_ShouldBeRefused_WhenTheSecretIsNotTheRuns()
    {
        WriteVersions();
        await using var connection = fixture.Connect('A', MultiServerAmiUsers.Full, secret: MultiServerAmiUsers.NewSecret());

        var connect = async () => await connection.ConnectAsync();

        await connect.Should().ThrowAsync<AmiAuthenticationException>(
            "only the run's generated secret admits the full user; the class assertions have their own controls");
    }

    [Fact]
    public async Task Trunk_ShouldCarryACallFromAToB_WhenACallIsOriginatedOnA()
    {
        WriteVersions();
        await using var observerA = fixture.Connect('A', MultiServerAmiUsers.Full);
        await using var observerB = fixture.Connect('B', MultiServerAmiUsers.Full);
        using var a = new AmiEventTally("A:full", observerA);
        using var b = new AmiEventTally("B:full", observerB);
        await observerA.ConnectAsync();
        await observerB.ConnectAsync();

        await observerA.SendActionAsync(new OriginateAction
        {
            Channel = $"Local/direct@{MultiServerFixture.LabContext}/n",
            Application = "Wait",
            Data = "3",
            IsAsync = true,
        });

        await b.WaitUntilAsync(t => t.Count("Hangup") >= 1, MultiServerTestFixture.CallBound, "B's Hangup of the trunk leg");
        await a.WaitUntilAsync(
            t => t.Count("Newchannel") > 0 && t.UniqueIds("Newchannel").IsSubsetOf(t.UniqueIds("Hangup")),
            MultiServerTestFixture.CallBound, "A's Hangup of every leg it created");
        await MultiServerTestFixture.DrainAsync(observerA, [a]);
        output.WriteLine(a.Describe());
        output.WriteLine(b.Describe());

        new
        {
            BNewchannel = b.Count("Newchannel"),
            BHangup = b.Count("Hangup"),
            AEveryLegEnded = a.UniqueIds("Newchannel").SetEquals(a.UniqueIds("Hangup")),
            ATrunkLegs = a.Channels("Newchannel").Count(c => c.StartsWith("PJSIP/agent-", StringComparison.Ordinal)),
        }.Should().BeEquivalentTo(
            new { BNewchannel = 1, BHangup = 1, AEveryLegEnded = true, ATrunkLegs = 1 },
            $"one call from A arrives on B as one channel that ends, and every leg A created ends. {a.Describe()}; {b.Describe()}");
    }

    [Fact]
    public async Task NoHangupUser_ShouldReceiveNoHangup_WhileFullReceivesOnePerLeg()
    {
        WriteVersions();
        var m = await fixture.MeasurementAsync();
        output.WriteLine(m.Description);
        const string full = "full", nohangup = "nohangup";

        new
        {
            FullHangup = m.A(full, "Hangup"),
            NoHangupHangup = m.A(nohangup, "Hangup"),
            NoHangupNewchannel = m.A(nohangup, "Newchannel"),
            NoHangupAgentConnect = m.A(nohangup, "AgentConnect"),
        }.Should().BeEquivalentTo(
            new
            {
                FullHangup = 3 * MultiServerTestFixture.Calls,
                NoHangupHangup = 0,
                NoHangupNewchannel = m.A(full, "Newchannel"),
                NoHangupAgentConnect = m.A(full, "AgentConnect"),
            },
            "the nohangup user's event filter removes Hangup and nothing else; full received one Hangup per leg "
            + $"(three per queue call) over the same calls.{Environment.NewLine}{m.Description}");
        m.A(full, "AgentConnect").Should().Be(MultiServerTestFixture.Calls, "every queue call was answered on B");
    }

    [Fact]
    public async Task NoAgentUser_ShouldReceiveNoAgentEvent_WhileFullReceivesOnePerCall()
    {
        WriteVersions();
        var m = await fixture.MeasurementAsync();
        output.WriteLine(m.Description);
        const string full = "full", noagent = "noagent";

        new
        {
            FullAgentConnect = m.A(full, "AgentConnect"),
            NoAgentAgentConnect = m.A(noagent, "AgentConnect"),
            NoAgentAgentCalled = m.A(noagent, "AgentCalled"),
            NoAgentQueueCallerJoin = m.A(noagent, "QueueCallerJoin"),
            NoAgentHangup = m.A(noagent, "Hangup"),
        }.Should().BeEquivalentTo(
            new
            {
                FullAgentConnect = MultiServerTestFixture.Calls,
                NoAgentAgentConnect = 0,
                NoAgentAgentCalled = 0,
                NoAgentQueueCallerJoin = 0,
                NoAgentHangup = m.A(full, "Hangup"),
            },
            "the noagent user lacks the agent read class, which carries every queue event, and keeps call; full "
            + $"received {m.A(full, "AgentCalled")} AgentCalled and {m.A(full, "QueueCallerJoin")} QueueCallerJoin over "
            + $"the same calls.{Environment.NewLine}{m.Description}");
        m.A(full, "AgentCalled").Should().BePositive();
        m.A(full, "QueueCallerJoin").Should().BePositive();
    }

    [Fact]
    public async Task ConsumerUser_ShouldReceiveItsClassesOnly_WhileFullReceivesDialplanEvents()
    {
        WriteVersions();
        var m = await fixture.MeasurementAsync();
        output.WriteLine(m.Description);
        const string full = "full", consumer = "consumer";

        new
        {
            ConsumerAgentConnect = m.A(consumer, "AgentConnect"),
            ConsumerHangup = m.A(consumer, "Hangup"),
            ConsumerNewexten = m.A(consumer, "Newexten"),
            ConsumerVarSet = m.A(consumer, "VarSet"),
        }.Should().BeEquivalentTo(
            new
            {
                ConsumerAgentConnect = m.A(full, "AgentConnect"),
                ConsumerHangup = m.A(full, "Hangup"),
                ConsumerNewexten = 0,
                ConsumerVarSet = 0,
            },
            "the consumer class set has call and agent but no dialplan class, which carries Newexten and VarSet; full "
            + $"received {m.A(full, "Newexten")} Newexten and {m.A(full, "VarSet")} VarSet over the same calls."
            + $"{Environment.NewLine}{m.Description}");
        m.A(full, "AgentConnect").Should().Be(MultiServerTestFixture.Calls);
        m.A(full, "Newexten").Should().BePositive();
        m.A(full, "VarSet").Should().BePositive();
    }

    [Fact]
    public async Task FarEnd_ShouldAnswerEveryQueueCall_WhenTheMeasurementRuns()
    {
        WriteVersions();
        var m = await fixture.MeasurementAsync();
        output.WriteLine(m.Description);

        new { Newchannel = m.ServerB["Newchannel"], Hangup = m.ServerB["Hangup"] }.Should().BeEquivalentTo(
            new { Newchannel = MultiServerTestFixture.Calls, Hangup = MultiServerTestFixture.Calls },
            $"each queue call's member leg arrives on B and ends there.{Environment.NewLine}{m.Description}");
    }

    [Fact]
    public async Task Sipp_ShouldPlaceACallThatCrossesTheTrunk_WhenItDialsServerA()
    {
        WriteVersions();
        await using var observerB = fixture.Connect('B', MultiServerAmiUsers.Full);
        using var b = new AmiEventTally("B:full", observerB);
        await observerB.ConnectAsync();

        var result = await _lab.Sipp.RunUacAsync("server-a", "777");
        output.WriteLine(result.Stdout);
        output.WriteLine(result.Stderr);
        await b.WaitUntilAsync(t => t.Count("Hangup") >= 1, MultiServerTestFixture.CallBound, "B's Hangup of SIPp's call");
        output.WriteLine(b.Describe());

        new
        {
            result.ExitCode,
            Successful = Cumulative(result.Stdout, "Successful call"),
            Failed = Cumulative(result.Stdout, "Failed call"),
            BNewchannel = b.Count("Newchannel"),
            BHangup = b.Count("Hangup"),
        }.Should().BeEquivalentTo(
            new { ExitCode = 0L, Successful = 1, Failed = 0, BNewchannel = 1, BHangup = 1 },
            $"SIPp's one call enters A and A carries it over the trunk to B's far number. {b.Describe()}");
    }

    private void WriteVersions()
    {
        output.WriteLine($"server A ({_lab.RunName}-a): {_lab.ServerAVersion}");
        output.WriteLine($"server B ({_lab.RunName}-b): {_lab.ServerBVersion}");
    }

    /// <summary>The cumulative column of SIPp's statistics row <paramref name="row"/>, or -1 when it is absent.</summary>
    private static int Cumulative(string stdout, string row)
    {
        var match = StatisticsRow().Matches(stdout).LastOrDefault(m => m.Groups["row"].Value == row);
        return match is null ? -1 : int.Parse(match.Groups["cumulative"].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(?<row>Successful call|Failed call)\s*\|\s*\d+\s*\|\s*(?<cumulative>\d+)")]
    private static partial Regex StatisticsRow();
}
