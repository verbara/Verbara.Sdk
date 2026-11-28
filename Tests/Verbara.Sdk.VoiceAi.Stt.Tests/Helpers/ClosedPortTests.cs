using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// <see cref="ClosedPort"/> must hand a test a port that refuses connections and that nobody else can
/// take while the test uses it. A port that is only free at the moment it is chosen can be bound by
/// another process before the test connects, and the test then reads that process's answer (an
/// HTTP <c>404</c>, say) instead of a refusal.
/// </summary>
/// <remarks>
/// The helper lives in <c>Verbara.Sdk.TestInfrastructure</c>, which has no test project of its own, so
/// its tests live beside <see cref="StalledUpgradeListenerTests"/>.
/// </remarks>
public sealed class ClosedPortTests
{
    [Fact]
    public void Reserve_ShouldKeepThePortFromACompetitor_WhileTheTestStillNeedsIt()
    {
        // Arrange
        var port = ClosedPort.Reserve();
        using var competitor = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Act
        var fault = Record.Exception(() => competitor.Bind(new IPEndPoint(IPAddress.Loopback, port)));

        // Assert
        fault.Should().BeOfType<SocketException>("the port must still be owned by the test")
            .Which.SocketErrorCode.Should().Be(SocketError.AddressAlreadyInUse);
    }
}
