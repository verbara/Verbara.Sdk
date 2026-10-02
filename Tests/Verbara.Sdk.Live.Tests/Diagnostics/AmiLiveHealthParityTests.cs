using Verbara.Sdk;
using Verbara.Sdk.Ami.Diagnostics;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Diagnostics;

/// <summary>
/// The <c>ami</c> and <c>live</c> health checks never disagree about the AMI connection: over every
/// <see cref="AmiConnectionState"/>, a live server with a loaded table reports the status the <c>ami</c> check reports for
/// the same connection. The states come from the enum itself, so a state added later fails here until both checks map it.
/// </summary>
public class AmiLiveHealthParityTests
{
    public static TheoryData<AmiConnectionState> EveryState
    {
        get
        {
            var data = new TheoryData<AmiConnectionState>();
            foreach (var state in Enum.GetValues<AmiConnectionState>())
                data.Add(state);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public async Task CheckHealthAsync_ShouldReportTheSameStatusAsTheLiveCheck_WhenTheConnectionIsInEachState(AmiConnectionState state)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(state);
        var server = new VerbaraServer(connection, Substitute.For<ILogger<VerbaraServer>>());
        // One channel loaded, so a live check that skipped the connection would answer Healthy.
        server.Channels.OnNewChannel("uid-1", "PJSIP/100-0001", ChannelState.Up, "100");

        var ami = await new AmiHealthCheck(connection).CheckHealthAsync(new HealthCheckContext());
        var live = await new LiveHealthCheck(server).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            ami.Status.Should().Be(live.Status, $"the ami and live checks read {state} alike");
            ami.Data.Should().ContainKey("amiState").WhoseValue.Should().Be(live.Data["amiState"],
                "both name the state the same way");
        }
    }
}
