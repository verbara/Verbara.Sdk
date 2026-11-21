using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Connection;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// The connection's internal signal that its delivery has passed its last lost event, raised subscriber by subscriber: a
/// subscriber that throws is logged at Warning and the subscribers after it are still told.
/// </summary>
/// <remarks>
/// The signal is raised directly, as the pump's watcher raises it, on a connection that is never connected: what is under
/// test is the fan-out, not the pump (see <c>AsyncEventPumpLossDeliveredTests</c>).
/// </remarks>
public sealed class AmiConnectionLossDeliveredThroughTests
{
    [Fact]
    public async Task LossDeliveredThrough_ShouldStillTellTheLaterSubscribersAndLogTheFailure_WhenOneThrows()
    {
        var logger = new SignalingLogger<AmiConnection>();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
        }), new PipedSocketFactory(), logger);
        var told = new List<long>();
        connection.LossDeliveredThrough += _ => throw new InvalidOperationException("first subscriber");
        connection.LossDeliveredThrough += told.Add;

        connection.RaiseLossDeliveredThrough(7);

        using (new AssertionScope())
        {
            told.Should().Equal([7L], "the subscriber after the one that threw is still told, with the session's ordinal");
            logger.Entries.Where(e => e.Line.Contains("[AMI] Loss-delivered handler error", StringComparison.Ordinal))
                .Should().ContainSingle("the failure is logged once").Which.Level.Should().Be(LogLevel.Warning);
        }
    }
}
