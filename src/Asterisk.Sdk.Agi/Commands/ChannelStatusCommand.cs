namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: CHANNEL STATUS [channelname]</summary>
public sealed class ChannelStatusCommand : AgiCommandBase
{
    public string? Channel { get; set; }

    public override string BuildCommand() =>
        Channel is not null ? $"CHANNEL STATUS {Channel}" : "CHANNEL STATUS";
}
