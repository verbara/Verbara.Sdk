namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: CHANNEL STATUS</summary>
public sealed class ChannelStatusCommand : AgiCommandBase
{
    public string? Channel { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "CHANNEL STATUS";
    }
}
