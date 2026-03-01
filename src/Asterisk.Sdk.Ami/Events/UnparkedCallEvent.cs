using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("UnparkedCall")]
public sealed class UnparkedCallEvent : ChannelEventBase
{
    public string? RetrieverChannel { get; set; }
    public int? RetrieverChannelState { get; set; }
    public string? RetrieverChannelStateDesc { get; set; }
    public string? RetrieverCallerIDNum { get; set; }
    public string? RetrieverCallerIDName { get; set; }
    public string? RetrieverConnectedLineNum { get; set; }
    public string? RetrieverConnectedLineName { get; set; }
    public string? RetrieverAccountCode { get; set; }
    public string? RetrieverContext { get; set; }
    public string? RetrieverExten { get; set; }
    public string? RetrieverPriority { get; set; }
    public string? RetrieverUniqueid { get; set; }
}

