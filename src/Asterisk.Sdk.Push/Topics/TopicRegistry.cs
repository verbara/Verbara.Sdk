using System.Collections.Concurrent;
using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Topics;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ITopicRegistry"/>.
/// Uses <see cref="ConcurrentDictionary{TKey,TValue}"/> so registrations
/// from multiple threads are safe without external locking.
/// </summary>
public sealed class TopicRegistry : ITopicRegistry
{
    private readonly ConcurrentDictionary<Type, string> _templates = new();

    /// <inheritdoc/>
    public void Register<TEvent>(string topicTemplate) where TEvent : IPushEvent
    {
        if (!_templates.TryAdd(typeof(TEvent), topicTemplate))
            throw new InvalidOperationException(
                $"A topic template for '{typeof(TEvent).FullName}' is already registered.");
    }

    /// <inheritdoc/>
    public bool TryGetTemplate(Type eventType, out string? topicTemplate) =>
        _templates.TryGetValue(eventType, out topicTemplate);

    /// <inheritdoc/>
    public TopicName? ResolveTopicName(Type eventType, params object[] args)
    {
        if (!_templates.TryGetValue(eventType, out var template))
            return null;

        var formatted = string.Format(null, template, args);
        return TopicName.Parse(formatted);
    }
}
