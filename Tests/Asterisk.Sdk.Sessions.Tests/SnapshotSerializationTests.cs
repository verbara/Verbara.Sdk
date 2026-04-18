using System.Text.Json;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.Serialization;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Sessions.Tests;

public class SnapshotSerializationTests
{
    private static CallSession CreateTestSession()
    {
        var session = new CallSession("sess-001", "linked-001", "server-1", CallDirection.Inbound)
        {
            CreatedAt = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
        };

        session.QueueName = "support";
        session.AgentId = "agent-42";
        session.AgentInterface = "SIP/1001";
        session.BridgeId = "bridge-xyz";
        session.Context = "from-external";
        session.Extension = "100";

        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-caller",
            Channel = "SIP/trunk-00001",
            Technology = "SIP",
            Role = ParticipantRole.Caller,
            CallerIdNum = "5551234567",
            CallerIdName = "John Doe",
            JoinedAt = session.CreatedAt,
        });

        session.AddEvent(new CallSessionEvent(
            session.CreatedAt,
            CallSessionEventType.Created,
            "SIP/trunk-00001",
            null,
            "Call created"));

        session.SetMetadata("campaign", "spring-sale");
        session.SetMetadata("priority", "high");

        // Advance state: Created -> Dialing -> Ringing -> Connected -> OnHold
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Ringing);
        session.Transition(CallSessionState.Connected);
        session.StartHold();
        session.Transition(CallSessionState.OnHold);

        return session;
    }

    [Fact]
    public void FromSession_ShouldCaptureAllFields_WhenSessionHasFullState()
    {
        var session = CreateTestSession();
        var snapshot = CallSessionSnapshot.FromSession(session);

        // Identity
        snapshot.SessionId.Should().Be("sess-001");
        snapshot.LinkedId.Should().Be("linked-001");
        snapshot.ServerId.Should().Be("server-1");

        // State
        snapshot.State.Should().Be(CallSessionState.OnHold);
        snapshot.Direction.Should().Be(CallDirection.Inbound);

        // Dialplan
        snapshot.Context.Should().Be("from-external");
        snapshot.Extension.Should().Be("100");

        // Call context
        snapshot.QueueName.Should().Be("support");
        snapshot.AgentId.Should().Be("agent-42");
        snapshot.AgentInterface.Should().Be("SIP/1001");
        snapshot.BridgeId.Should().Be("bridge-xyz");

        // Caller ID (from Participants[0])
        snapshot.CallerIdNum.Should().Be("5551234567");
        snapshot.CallerIdName.Should().Be("John Doe");

        // Timestamps
        snapshot.CreatedAt.Should().Be(new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero));
        snapshot.DialingAt.Should().NotBeNull();
        snapshot.RingingAt.Should().NotBeNull();
        snapshot.ConnectedAt.Should().NotBeNull();

        // Hold
        snapshot.HoldStartedAt.Should().NotBeNull();

        // Collections
        snapshot.Participants.Should().HaveCount(1);
        snapshot.Participants[0].CallerIdNum.Should().Be("5551234567");
        snapshot.Events.Should().HaveCount(1);
        snapshot.Metadata.Should().ContainKey("campaign").WhoseValue.Should().Be("spring-sale");
        snapshot.Metadata.Should().ContainKey("priority").WhoseValue.Should().Be("high");
    }

    [Fact]
    public void JsonRoundtrip_ShouldPreserveAllFields_WhenSerializedAndDeserialized()
    {
        var session = CreateTestSession();
        var original = CallSessionSnapshot.FromSession(session);

        var json = JsonSerializer.Serialize(original, SessionJsonContext.Default.CallSessionSnapshot);
        var deserialized = JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot);

        deserialized.Should().NotBeNull();
        deserialized!.SessionId.Should().Be(original.SessionId);
        deserialized.LinkedId.Should().Be(original.LinkedId);
        deserialized.ServerId.Should().Be(original.ServerId);
        deserialized.State.Should().Be(original.State);
        deserialized.Direction.Should().Be(original.Direction);
        deserialized.Context.Should().Be(original.Context);
        deserialized.Extension.Should().Be(original.Extension);
        deserialized.QueueName.Should().Be(original.QueueName);
        deserialized.AgentId.Should().Be(original.AgentId);
        deserialized.AgentInterface.Should().Be(original.AgentInterface);
        deserialized.BridgeId.Should().Be(original.BridgeId);
        deserialized.CallerIdNum.Should().Be(original.CallerIdNum);
        deserialized.CallerIdName.Should().Be(original.CallerIdName);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);
        deserialized.DialingAt.Should().Be(original.DialingAt);
        deserialized.RingingAt.Should().Be(original.RingingAt);
        deserialized.ConnectedAt.Should().Be(original.ConnectedAt);
        deserialized.HoldStartedAt.Should().Be(original.HoldStartedAt);
        deserialized.AccumulatedHoldTime.Should().Be(original.AccumulatedHoldTime);
        deserialized.Participants.Should().HaveCount(original.Participants.Count);
        deserialized.Events.Should().HaveCount(original.Events.Count);
        deserialized.Metadata.Should().BeEquivalentTo(original.Metadata);
    }

    [Fact]
    public void ToSession_ShouldReconstructFromSnapshot_WhenSnapshotIsComplete()
    {
        var session = CreateTestSession();
        var snapshot = CallSessionSnapshot.FromSession(session);
        var reconstructed = snapshot.ToSession();

        reconstructed.SessionId.Should().Be("sess-001");
        reconstructed.LinkedId.Should().Be("linked-001");
        reconstructed.ServerId.Should().Be("server-1");
        reconstructed.State.Should().Be(CallSessionState.OnHold);
        reconstructed.Direction.Should().Be(CallDirection.Inbound);
        reconstructed.QueueName.Should().Be("support");
        reconstructed.AgentId.Should().Be("agent-42");
        reconstructed.Participants.Should().HaveCount(1);
        reconstructed.Events.Should().HaveCount(1);
        reconstructed.Metadata.Should().ContainKey("campaign");
    }

    [Fact]
    public void FullRoundtrip_ShouldPreserveAllFields_WhenSessionToSnapshotToJsonToSnapshotToSession()
    {
        var original = CreateTestSession();

        // Session -> Snapshot -> JSON -> Snapshot -> Session
        var snapshot1 = CallSessionSnapshot.FromSession(original);
        var json = JsonSerializer.Serialize(snapshot1, SessionJsonContext.Default.CallSessionSnapshot);
        var snapshot2 = JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot)!;
        var reconstructed = snapshot2.ToSession();

        // Verify identity
        reconstructed.SessionId.Should().Be(original.SessionId);
        reconstructed.LinkedId.Should().Be(original.LinkedId);
        reconstructed.ServerId.Should().Be(original.ServerId);

        // Verify state
        reconstructed.State.Should().Be(original.State);
        reconstructed.Direction.Should().Be(original.Direction);

        // Verify collections
        reconstructed.Participants.Should().HaveCount(original.Participants.Count);
        reconstructed.Participants[0].CallerIdNum.Should().Be(original.Participants[0].CallerIdNum);
        reconstructed.Events.Should().HaveCount(original.Events.Count);
        reconstructed.Metadata.Should().HaveCount(original.Metadata.Count);
    }

    [Fact]
    public void AllEnums_ShouldSerializeAsStrings_WhenSnapshotIsSerialized()
    {
        var session = new CallSession("sess-enum", "linked-enum", "server-1", CallDirection.Outbound)
        {
            CreatedAt = DateTimeOffset.UtcNow,
        };
        session.HangupCause = HangupCause.NormalClearing;
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);
        session.StartHold();
        session.Transition(CallSessionState.OnHold);

        var snapshot = CallSessionSnapshot.FromSession(session);
        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);

        // camelCase naming policy applies to property names, but UseStringEnumConverter
        // serializes enum values as their member names
        json.Should().Contain("OnHold");
        json.Should().Contain("Outbound");
        json.Should().Contain("NormalClearing");

        // Should NOT contain integer representations
        json.Should().NotContain("\"state\":5");
        json.Should().NotContain("\"direction\":1");
    }

    [Fact]
    public async Task PrivateHoldState_ShouldSurviveRoundtrip_WhenSessionHasAccumulatedHold()
    {
        var session = new CallSession("sess-hold", "linked-hold", "server-1", CallDirection.Inbound)
        {
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);

        // First hold cycle
        session.StartHold();
        session.Transition(CallSessionState.OnHold);
        await Task.Delay(15);
        session.EndHold();
        session.Transition(CallSessionState.Connected);

        // Second hold cycle (still holding at snapshot time)
        session.StartHold();
        session.Transition(CallSessionState.OnHold);

        var snapshot = CallSessionSnapshot.FromSession(session);

        // Verify accumulated time from first hold
        snapshot.AccumulatedHoldTime.Should().BeGreaterThan(TimeSpan.Zero);
        // Verify current hold is tracked
        snapshot.HoldStartedAt.Should().NotBeNull();

        // JSON roundtrip
        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);
        var deserialized = JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot)!;

        deserialized.AccumulatedHoldTime.Should().Be(snapshot.AccumulatedHoldTime);
        deserialized.HoldStartedAt.Should().Be(snapshot.HoldStartedAt);
    }
}
