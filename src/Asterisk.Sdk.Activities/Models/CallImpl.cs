namespace Asterisk.Sdk.Activities.Models;

/// <summary>Concrete implementation of a call.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Domain term - represents a phone call")]
public sealed class Call : ICall
{
    private readonly List<PbxChannel> _channels = [];

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public CallDirection Direction { get; init; }
    public CallState State { get; set; } = CallState.New;
    public IReadOnlyList<IPbxChannel> Channels => _channels.AsReadOnly();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public void AddChannel(PbxChannel channel) => _channels.Add(channel);
    public void RemoveChannel(string uniqueId) => _channels.RemoveAll(c => c.UniqueId == uniqueId);
}

/// <summary>Concrete implementation of a PBX channel.</summary>
public sealed class PbxChannel : IPbxChannel
{
    public string Name { get; init; } = string.Empty;
    public string UniqueId { get; init; } = string.Empty;
    public EndPoint? EndPoint { get; init; }
}
