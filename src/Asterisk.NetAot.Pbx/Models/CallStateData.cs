namespace Asterisk.NetAot.Pbx.Models;

/// <summary>Base class for call state data.</summary>
public abstract class CallStateData
{
    public CallState State { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>State data when a call is answered.</summary>
public sealed class CallStateAnswered : CallStateData
{
    public string? AnsweringChannel { get; init; }
    public CallStateAnswered() { State = CallState.Answered; }
}

/// <summary>State data for a new inbound call.</summary>
public sealed class CallStateNewInbound : CallStateData
{
    public string? CallerIdNum { get; init; }
    public string? CallerIdName { get; init; }
    public string? DestinationContext { get; init; }
    public string? DestinationExtension { get; init; }
    public CallStateNewInbound() { State = CallState.New; }
}

/// <summary>State data when a call is parked.</summary>
public sealed class CallStateParked : CallStateData
{
    public string? ParkingLot { get; init; }
    public string? ParkingSpace { get; init; }
    public CallStateParked() { State = CallState.Parked; }
}

/// <summary>State data when a call is being transferred.</summary>
public sealed class CallStateTransfer : CallStateData
{
    public string? TransferTarget { get; init; }
    public bool IsBlind { get; init; }
    public CallStateTransfer() { State = CallState.Transferred; }
}
