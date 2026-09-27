using FluentAssertions;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the twelve-shape AMI captures of Asterisk 20.20.1, 22.9.0 and 23.4.1
/// (<c>Recordings/asterisk-ami/</c>) through the production parsing path into
/// <see cref="Live.Server.VerbaraServer"/>'s observer and a <see cref="Manager.CallSessionManager"/>,
/// and binds what the SDK does with the bytes Asterisk really sent.
/// </summary>
/// <remarks>
/// For an AMI <c>Originate</c>, Asterisk's <c>DialBegin</c> and <c>DialEnd</c> name only the dialed
/// side (<c>DestChannel</c>, <c>DestUniqueid</c>): there is no calling channel and no <c>Uniqueid</c>.
/// Five of the twelve scenarios carry that shape (S3, S4, S5, S6, S7).
/// </remarks>
public sealed class CallShapeCaptureReplayTests
{
    public static TheoryData<string> CallShapeCaptures => new(AmiCaptureReplay.CallShapeCaptures);

    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task ServerObserver_ShouldThrowNothingIntoTheDispatcher_WhenACallShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);

        replay.Calls.Should().HaveCount(12, "each of the twelve scenarios is one call. {0}", replay.Describe());

        var swallowed = replay.ObserverExceptions.Select(e => e.ToString()).ToList();
        swallowed.Should().BeEmpty(
            "a dial event that names no calling channel is skipped, not thrown into the AMI dispatcher; "
            + "the dispatcher swallowed {0}: {1}",
            swallowed.Count, string.Join(" | ", swallowed));
    }
}
