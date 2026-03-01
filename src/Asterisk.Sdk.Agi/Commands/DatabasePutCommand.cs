namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: DATABASE PUT family key value</summary>
public sealed class DatabasePutCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }

    public override string BuildCommand() => $"DATABASE PUT {Family} {Key} {Value}";
}
