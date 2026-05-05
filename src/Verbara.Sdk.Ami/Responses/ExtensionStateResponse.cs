using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[VerbaraMapping("ExtensionState")]
public sealed class ExtensionStateResponse : ManagerResponse
{
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public string? Hint { get; set; }
    public int? Status { get; set; }
    public string? StatusText { get; set; }
}

