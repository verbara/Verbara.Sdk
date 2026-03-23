namespace PbxAdmin.Models;

// Notes:
// - EntityId is string for uniformity: routes/TCs/IVRs use int Id (.ToString()),
//   queues and extensions use name as ID.
// - These types are never serialized over SignalR — used only in-memory for Blazor rendering.
// - CallFlowTrace and CallFlowTraceStep are deferred to Phase 2.

public abstract class CallFlowNode
{
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string Label { get; init; } = "";
    public string? EditUrl { get; init; }
    public List<string> DialplanLines { get; init; } = [];
}

public sealed class DidNode : CallFlowNode
{
    public string DidPattern { get; init; } = "";
    public string RouteName { get; init; } = "";
    public int Priority { get; init; }
    public CallFlowNode? Destination { get; init; }
}

public sealed class TimeConditionNode : CallFlowNode
{
    public string ScheduleSummary { get; init; } = "";
    public string CurrentState { get; init; } = "";
    public CallFlowNode? OpenBranch { get; init; }
    public CallFlowNode? ClosedBranch { get; init; }
}

public sealed class IvrNode : CallFlowNode
{
    public string? Greeting { get; init; }
    public int Timeout { get; init; }
    public List<IvrOptionNode> Options { get; init; } = [];
}

public sealed class IvrOptionNode
{
    public string Digit { get; init; } = "";
    public string? OptionLabel { get; init; }
    public CallFlowNode? Destination { get; init; }
}

public sealed class QueueNode : CallFlowNode
{
    public string Strategy { get; init; } = "";
    public int MemberCount { get; init; }
    public int OnlineCount { get; init; }
}

public sealed class ExtensionNode : CallFlowNode
{
    public string Number { get; init; } = "";
    public string? DisplayName { get; init; }
    public bool IsRegistered { get; init; }
    public string Technology { get; init; } = "";
}

public sealed class VoicemailNode : CallFlowNode
{
    public string Extension { get; init; } = "";
    public string? Email { get; init; }
}

public sealed class HangupNode : CallFlowNode { }

public sealed class CallFlowGraph
{
    public string ServerId { get; init; } = "";
    public DateTime BuiltAt { get; init; }
    public List<DidNode> InboundFlows { get; init; } = [];
    public List<HealthWarning> Warnings { get; init; } = [];
}

public sealed class HealthWarning
{
    public string Severity { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string? NavigateUrl { get; init; }
}

public sealed class CrossReference
{
    public string SourceType { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string SourceLabel { get; init; } = "";
    public string Relationship { get; init; } = "";
}
