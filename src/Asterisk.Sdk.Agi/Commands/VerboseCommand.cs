using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: VERBOSE "message" [level]</summary>
public sealed class VerboseCommand : AgiCommandBase
{
    public string? Message { get; set; }
    public int? Level { get; set; }

    public override string BuildCommand()
    {
        var escaped = EscapeQuotes(Message);

        return Level.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"VERBOSE \"{escaped}\" {Level.Value}")
            : $"VERBOSE \"{escaped}\"";
    }

    private static string EscapeQuotes(string? value) =>
        value?.Replace("\"", "\\\"") ?? string.Empty;
}
