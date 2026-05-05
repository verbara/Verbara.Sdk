using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

/// <summary>
/// Response to the Command AMI action.
/// Contains the command output text in addition to standard response fields.
/// </summary>
[VerbaraMapping("Command")]
public sealed class CommandResponse : ManagerResponse
{
    public string? Privilege { get; set; }

    /// <summary>
    /// The text output of the executed command, extracted from the __CommandOutput raw field.
    /// </summary>
    public string? Output =>
        RawFields is not null && RawFields.TryGetValue("__CommandOutput", out var output)
            ? output
            : null;
}
