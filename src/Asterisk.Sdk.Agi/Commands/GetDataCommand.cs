using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GET DATA file [timeout [maxdigits]]</summary>
public sealed class GetDataCommand : AgiCommandBase
{
    public string? File { get; set; }
    public long? Timeout { get; set; }
    public int? MaxDigits { get; set; }

    public override string BuildCommand()
    {
        if (!Timeout.HasValue)
            return $"GET DATA {File}";

        if (!MaxDigits.HasValue)
            return string.Create(CultureInfo.InvariantCulture, $"GET DATA {File} {Timeout.Value}");

        return string.Create(CultureInfo.InvariantCulture, $"GET DATA {File} {Timeout.Value} {MaxDigits.Value}");
    }
}
