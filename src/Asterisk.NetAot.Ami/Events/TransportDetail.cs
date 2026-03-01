using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("TransportDetail")]
public sealed class TransportDetail : ResponseEvent
{
    public string? ObjectType { get; set; }
    public string? PbjectName { get; set; }
    public string? Protocol { get; set; }
    public string? Bind { get; set; }
    public int? AsycOperations { get; set; }
    public string? CaListFile { get; set; }
    public string? CaListPath { get; set; }
    public string? CertFile { get; set; }
    public string? PrivKeyFile { get; set; }
    public string? Password { get; set; }
    public string? ExternalSignalingAddress { get; set; }
    public int? ExternalSignalingPort { get; set; }
    public string? ExternalMediaAddress { get; set; }
    public string? Domain { get; set; }
    public string? VerifyServer { get; set; }
    public string? VerifyClient { get; set; }
    public bool? RequireClientCert { get; set; }
    public string? Method { get; set; }
    public string? Cipher { get; set; }
    public string? LocalNet { get; set; }
    public string? Tos { get; set; }
    public int? Cos { get; set; }
    public int? WebsocketWriteTimeout { get; set; }
    public string? EndpointName { get; set; }
}

