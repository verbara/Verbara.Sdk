namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC Bridge — bridges the current channel to another channel or endpoint.</summary>
public sealed class BridgeCommand : AgiCommandBase
{
    public string? Channel { get; set; }
    public string? Options { get; set; }

    public override string BuildCommand() =>
        Options is not null ? $"EXEC Bridge {Channel},{Options}" : $"EXEC Bridge {Channel}";
}
