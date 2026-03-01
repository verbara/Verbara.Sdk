namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: HANGUP [channelname]</summary>
public sealed class HangupCommand : AgiCommandBase
{
    public string? Channel { get; set; }

    public override string BuildCommand() =>
        Channel is not null ? $"HANGUP {Channel}" : "HANGUP";
}
