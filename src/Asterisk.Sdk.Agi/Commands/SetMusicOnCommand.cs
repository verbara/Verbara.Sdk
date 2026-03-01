namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET MUSIC ON</summary>
public sealed class SetMusicOnCommand : AgiCommandBase
{
    public string? MusicOnHoldClass { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SET MUSIC ON";
    }
}
