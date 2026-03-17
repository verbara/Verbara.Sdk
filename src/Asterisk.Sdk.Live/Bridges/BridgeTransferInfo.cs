namespace Asterisk.Sdk.Live.Bridges;

/// <summary>
/// Carries information about a blind or attended transfer involving a bridge.
/// </summary>
public sealed record BridgeTransferInfo(
    string BridgeId,
    string TransferType,
    string? TargetChannel,
    string? SecondBridgeId,
    string? DestType,
    string? Result);
