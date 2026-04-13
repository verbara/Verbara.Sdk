using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Topics;

/// <summary>
/// Maps .NET event types to topic templates. When a push event is published,
/// the registry resolves its type to a concrete <see cref="TopicName"/> by
/// formatting the registered template with caller-supplied resource IDs.
/// </summary>
public interface ITopicRegistry
{
    /// <summary>
    /// Registers a topic template for a specific event type.
    /// Template uses <c>{0}</c>, <c>{1}</c>, … positional parameters for resource IDs.
    /// Example: <c>"conversation.{0}.assigned"</c> where <c>{0}</c> = conversationId.
    /// </summary>
    /// <typeparam name="TEvent">The push event type to associate with the template.</typeparam>
    /// <param name="topicTemplate">A composite-format string with positional placeholders.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <typeparamref name="TEvent"/> is already registered.
    /// </exception>
    void Register<TEvent>(string topicTemplate) where TEvent : IPushEvent;

    /// <summary>
    /// Resolves the topic template registered for <paramref name="eventType"/>.
    /// </summary>
    /// <param name="eventType">The event <see cref="Type"/> to look up.</param>
    /// <param name="topicTemplate">
    /// The template string when found; <see langword="null"/> otherwise.
    /// </param>
    /// <returns><see langword="true"/> if a template is registered; otherwise <see langword="false"/>.</returns>
    bool TryGetTemplate(Type eventType, out string? topicTemplate);

    /// <summary>
    /// Resolves a concrete <see cref="TopicName"/> by formatting the registered template
    /// with the supplied <paramref name="args"/>.
    /// </summary>
    /// <param name="eventType">The event <see cref="Type"/> to resolve.</param>
    /// <param name="args">Positional arguments substituted into the template.</param>
    /// <returns>
    /// A formatted <see cref="TopicName"/>, or <see langword="null"/> if no template
    /// is registered for <paramref name="eventType"/>.
    /// </returns>
    TopicName? ResolveTopicName(Type eventType, params object[] args);
}
