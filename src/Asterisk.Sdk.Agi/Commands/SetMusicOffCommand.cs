namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET MUSIC OFF</summary>
public sealed class SetMusicOffCommand : AgiCommandBase
{
    public override string BuildCommand() => "SET MUSIC OFF";
}
