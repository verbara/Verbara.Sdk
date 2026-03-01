namespace Asterisk.Sdk.Ami.Actions;

/// <summary>
/// Marker interface for actions that generate response events
/// (e.g., QueueStatusAction generates QueueParams + QueueMember + QueueStatusComplete events).
/// </summary>
public interface IEventGeneratingAction;
