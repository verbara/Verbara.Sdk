using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("LocalOptimizationEnd")]
public sealed class LocalOptimizationEndEvent : ManagerEvent
{
    public int? Id { get; set; }
    /// <summary>Asterisk's <c>Success</c> header, verbatim; not an outcome flag.</summary>
    /// <remarks>
    /// This is Asterisk's <c>Success</c> header, verbatim. For a swap optimization Asterisk reports
    /// <c>No</c> when the move completed and <c>Yes</c> when it did not; for a merge optimization it
    /// reports <c>Yes</c> when it completed. A completed optimization is shown by the Local channels'
    /// hangup.
    /// </remarks>
    public bool? Success { get; set; }
    public string? LocalOneChannel { get; set; }
    public string? LocalOneChannelState { get; set; }
    public string? LocalOneChannelStateDesc { get; set; }
    public string? LocalOneCallerIDNum { get; set; }
    public string? LocalOneCallerIDName { get; set; }
    public string? LocalOneConnectedLineNum { get; set; }
    public string? LocalOneConnectedLineName { get; set; }
    public string? LocalOneAccountCode { get; set; }
    public string? LocalOneContext { get; set; }
    public string? LocalOneExten { get; set; }
    public int? LocalOnePriority { get; set; }
    public string? LocalOneUniqueid { get; set; }
    public string? LocalOneLinkedid { get; set; }
    public string? LocalTwoChannel { get; set; }
    public string? LocalTwoChannelState { get; set; }
    public string? LocalTwoChannelStateDesc { get; set; }
    public string? LocalTwoCallerIDNum { get; set; }
    public string? LocalTwoCallerIDName { get; set; }
    public string? LocalTwoConnectedLineNum { get; set; }
    public string? LocalTwoConnectedLineName { get; set; }
    public string? LocalTwoAccountCode { get; set; }
    public string? LocalTwoContext { get; set; }
    public string? LocalTwoExten { get; set; }
    public int? LocalTwoPriority { get; set; }
    public string? LocalTwoUniqueid { get; set; }
    public string? LocalTwoLinkedid { get; set; }
    public string? LocalTwoLanguage { get; set; }
    public string? LocalOneLanguage { get; set; }
}

