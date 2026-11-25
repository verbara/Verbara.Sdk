using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// A call another process reconstructed and registered in a connected state, which then joins a queue and whose caller
/// hangs up while it waits: it took place, as a call a reload opened connected did, and ends completed on a normal
/// clearing. The frames are synthetic, parsed by the SDK's own reader: the server's start load lists the caller's
/// channel up, and the live frames are the caller's <c>QueueCallerJoin</c> and its <c>Hangup</c>.
/// </summary>
public sealed class ReconstructedCallEndingTests
{
    private const string Caller = "rebuilt-c1";
    private const string CallerChannel = "PJSIP/trunk-00000001";

    [Theory]
    [InlineData(CallSessionState.Connected)]
    [InlineData(CallSessionState.Transferring)]
    public async Task CallSession_ShouldEndCompleted_WhenACallRegisteredConnectedHangsUpNormallyWhileWaitingInAQueue(
        CallSessionState registeredIn)
    {
        var connection = new ReloadableConnection(await FramesAsync(
            "Event: Status\r\nPrivilege: Call\r\nChannel: " + CallerChannel + "\r\nChannelState: 6\r\nChannelStateDesc: Up\r\n"
            + "Uniqueid: " + Caller + "\r\nLinkedid: " + Caller + "\r\nActionID: start-status\r\n\r\n"
            + "Event: StatusComplete\r\nActionID: start-status\r\nEventList: Complete\r\nListItems: 1\r\nItems: 1\r\n\r\n"));
        var server = new VerbaraServer(connection.Connection, NullLogger<VerbaraServer>.Instance);
        var manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        var call = new CallSession("rebuilt-session", Caller, "srv", CallDirection.Inbound) { State = registeredIn };
        call.AddParticipant(new SessionParticipant
        {
            UniqueId = Caller, Channel = CallerChannel, Technology = "PJSIP", Role = ParticipantRole.Caller,
        });
        var endings = new List<CallEndedEvent>();
        try
        {
            using var subscription = manager.Events.Subscribe(e =>
            {
                if (e is CallEndedEvent ended) endings.Add(ended);
            });
            manager.RegisterReconstructedSession(call).Should().BeTrue("premise: the registration is accepted");
            manager.AttachToServer(server, "srv");
            await server.StartAsync();
            foreach (var frame in await FramesAsync(
                "Event: QueueCallerJoin\r\nPrivilege: agent,all\r\nChannel: " + CallerChannel + "\r\nUniqueid: " + Caller
                + "\r\nLinkedid: " + Caller + "\r\nQueue: support\r\nPosition: 1\r\nCount: 1\r\n\r\n"))
                connection.Observer.OnNext(frame);
            var waiting = call.State;
            foreach (var frame in await FramesAsync(
                "Event: Hangup\r\nPrivilege: call,all\r\nChannel: " + CallerChannel + "\r\nUniqueid: " + Caller
                + "\r\nLinkedid: " + Caller + "\r\nCause: 16\r\nCause-txt: Normal Clearing\r\n\r\n"))
                connection.Observer.OnNext(frame);

            using var scope = new AssertionScope();
            waiting.Should().Be(CallSessionState.Queued, "premise: the call waits in the queue it joined");
            call.State.Should().Be(CallSessionState.Completed,
                "a call registered in a state only a connection reaches took place, and its caller hung up normally");
            call.HangupCause.Should().Be(HangupCause.NormalClearing, "the call's cause is the caller's own channel's");
            endings.Should().ContainSingle("the call ends once")
                .Which.FinalState.Should().Be(CallSessionState.Completed, "the ending carries the state the call ended in");
        }
        finally
        {
            await manager.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static Task<IReadOnlyList<ManagerEvent>> FramesAsync(string frames) =>
        AmiCaptureReplay.ReadCaptureAsync(Encoding.Latin1.GetBytes("Asterisk Call Manager/11.0.0\r\n" + frames));
}
