using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("ExtensionState")]
public sealed class ExtensionStateResponse : ManagerResponse
{
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public string? Hint { get; set; }
    public int? Status { get; set; }
    public string? StatusText { get; set; }
}

