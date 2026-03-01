namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET MUSIC ON [class]</summary>
public sealed class SetMusicOnCommand : AgiCommandBase
{
    public string? MusicOnHoldClass { get; set; }

    public override string BuildCommand() =>
        MusicOnHoldClass is not null ? $"SET MUSIC ON {MusicOnHoldClass}" : "SET MUSIC ON";
}
