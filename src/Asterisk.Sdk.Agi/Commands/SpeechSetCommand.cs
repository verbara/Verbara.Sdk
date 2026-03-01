namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH SET name value</summary>
public sealed class SpeechSetCommand : AgiCommandBase
{
    public string? Name { get; set; }
    public string? Value { get; set; }

    public override string BuildCommand() => $"SPEECH SET {Name} {Value}";
}
