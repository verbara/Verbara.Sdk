using Verbara.Sdk;

namespace Verbara.Sdk.Ami.Events;

/// <summary>
/// Base class for events that are generated in response to an action
/// (e.g., QueueParamsEvent in response to QueueStatusAction).
/// </summary>
public class ResponseEvent : ManagerEvent
{
    public string? ActionId { get; set; }
}
