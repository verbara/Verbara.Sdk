namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SEND TEXT "text"</summary>
public sealed class SendTextCommand : AgiCommandBase
{
    public string? Text { get; set; }

    public override string BuildCommand() => $"SEND TEXT \"{EscapeQuotes(Text)}\"";

    private static string EscapeQuotes(string? value) =>
        value?.Replace("\"", "\\\"") ?? string.Empty;
}
