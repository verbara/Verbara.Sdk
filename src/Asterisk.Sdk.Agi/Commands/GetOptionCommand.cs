using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GET OPTION filename escapeDigits [timeout]</summary>
public sealed class GetOptionCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? EscapeDigits { get; set; }
    public long? Timeout { get; set; }

    public override string BuildCommand() =>
        Timeout.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"GET OPTION {File} {EscapeDigits} {Timeout.Value}")
            : $"GET OPTION {File} {EscapeDigits}";
}
