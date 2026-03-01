using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Responses;

[AsteriskMapping("GetConfig")]
public sealed class GetConfigResponse : ManagerResponse
{
}

