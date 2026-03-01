using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: STREAM FILE filename escapeDigits [sample offset]</summary>
public sealed class StreamFileCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? EscapeDigits { get; set; }
    public int? Offset { get; set; }

    public override string BuildCommand() =>
        Offset.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"STREAM FILE {File} {EscapeDigits} {Offset.Value}")
            : $"STREAM FILE {File} {EscapeDigits}";
}
