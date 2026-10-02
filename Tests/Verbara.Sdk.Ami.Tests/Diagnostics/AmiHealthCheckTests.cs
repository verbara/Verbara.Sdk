using Verbara.Sdk;
using Verbara.Sdk.Ami.Diagnostics;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Verbara.Sdk.Ami.Tests.Diagnostics;

/// <summary>
/// The <c>ami</c> health check reads the AMI connection's state with the table the <c>live</c> check reads it with:
/// <see cref="AmiConnectionState.Connected"/> is <see cref="HealthStatus.Healthy"/>; a connection that may still come up
/// on its own — <see cref="AmiConnectionState.Reconnecting"/>, <see cref="AmiConnectionState.Connecting"/> — and one
/// nobody has connected yet — <see cref="AmiConnectionState.Initial"/> — is <see cref="HealthStatus.Degraded"/>; one
/// that nothing reconnects until a new connect — <see cref="AmiConnectionState.Disconnecting"/>,
/// <see cref="AmiConnectionState.Disconnected"/> — is <see cref="HealthStatus.Unhealthy"/>. The result's data carries
/// the state's name as a string under <c>amiState</c>.
/// </summary>
public class AmiHealthCheckTests
{
    /// <summary>Every state, with the status the check reports for it.</summary>
    public static TheoryData<AmiConnectionState, HealthStatus> Table => new()
    {
        { AmiConnectionState.Initial, HealthStatus.Degraded },
        { AmiConnectionState.Connecting, HealthStatus.Degraded },
        { AmiConnectionState.Connected, HealthStatus.Healthy },
        { AmiConnectionState.Reconnecting, HealthStatus.Degraded },
        { AmiConnectionState.Disconnecting, HealthStatus.Unhealthy },
        { AmiConnectionState.Disconnected, HealthStatus.Unhealthy },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public async Task CheckHealthAsync_ShouldReportTheLiveTablesStatusAndTheStateName_WhenTheConnectionIsInEachState(
        AmiConnectionState state, HealthStatus expected)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(state);
        var check = new AmiHealthCheck(connection);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(expected, $"the ami check reads {state} as the live check does");
            result.Data.Should().ContainKey("amiState", "the result names the state it was read from")
                .WhoseValue.Should().BeOfType<string>("a reflection-free JSON writer cannot serialize a boxed enum")
                .Which.Should().Be(state.ToString());
        }
    }

    [Fact]
    public void Table_ShouldMapEveryState_WhenAStateIsAdded()
    {
        IEnumerable<object[]> rows = Table;

        rows.Select(row => (AmiConnectionState)row[0]).Should().BeEquivalentTo(Enum.GetValues<AmiConnectionState>(),
            "a state added later has a row here, and a status, before it ships");
    }
}
