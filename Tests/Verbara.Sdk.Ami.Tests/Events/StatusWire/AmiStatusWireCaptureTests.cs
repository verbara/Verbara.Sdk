using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// The captured <c>Status</c> replies are still the bytes Asterisk sent: CRLF-framed throughout, six
/// <c>Event: Status</c> frames per version. A checkout that converted line endings, or a fixture edited by hand,
/// fails here before any parser test reads it.
/// </summary>
public sealed class AmiStatusWireCaptureTests
{
    public static TheoryData<string> Versions() => StatusWireReplies.Versions();

    [Theory]
    [MemberData(nameof(Versions))]
    public void StatusReply_ShouldBeCrlfFramedWithSixStatusFrames_WhenReadForAnyCapturedVersion(string version)
    {
        var bytes = AmiStatusWireCapture.StatusReply(version);
        var text = Encoding.ASCII.GetString(bytes);

        using var scope = new AssertionScope();
        bytes.Should().NotBeEmpty();
        text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Should().NotContain("\n",
            "AMI frames every line with CRLF; a bare LF means the capture was line-ending converted");
        var frames = text.Split("\r\n\r\n").Count(f => f.StartsWith("Event: Status\r\n", StringComparison.Ordinal));
        frames.Should().Be(AmiStatusWireCapture.StatusFramesPerVersion,
            $"Asterisk {version} listed the six legs of the probe");
    }

    [Fact]
    public void Versions_ShouldBeTheFourMeasuredReleases_WhenListed()
    {
        AmiStatusWireCapture.Versions.Should().Equal("18.26.4", "20.20.1", "22.9.0", "23.4.1");
    }
}
