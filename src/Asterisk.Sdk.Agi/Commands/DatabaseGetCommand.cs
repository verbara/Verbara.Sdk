namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: DATABASE GET family key</summary>
public sealed class DatabaseGetCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? Key { get; set; }

    public override string BuildCommand() => $"DATABASE GET {Family} {Key}";
}
