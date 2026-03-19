namespace Asterisk.Sdk.VoiceAi.Testing;

/// <summary>
/// A fake <see cref="IConversationHandler"/> that returns pre-configured responses.
/// Useful for testing Voice AI pipelines without a real LLM or conversation backend.
/// </summary>
public sealed class FakeConversationHandler : IConversationHandler
{
    private readonly List<string> _responses = [];
    private TimeSpan _delay;
    private int _callIndex;
    private readonly List<string> _receivedTranscripts = [];

    /// <summary>Number of times <see cref="HandleAsync"/> has been called.</summary>
    public int CallCount => _callIndex;

    /// <summary>All transcripts that were passed to <see cref="HandleAsync"/>, in order.</summary>
    public IReadOnlyList<string> ReceivedTranscripts => _receivedTranscripts;

    /// <summary>Configures a single response to return on every call.</summary>
    public FakeConversationHandler WithResponse(string response) { _responses.Add(response); return this; }

    /// <summary>Configures multiple responses that cycle round-robin across calls.</summary>
    public FakeConversationHandler WithResponses(IEnumerable<string> responses)
    {
        foreach (var r in responses) _responses.Add(r);
        return this;
    }

    /// <summary>Adds an artificial delay before returning responses.</summary>
    public FakeConversationHandler WithDelay(TimeSpan delay) { _delay = delay; return this; }

    /// <inheritdoc />
    public async ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default)
    {
        _receivedTranscripts.Add(transcript);

        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct).ConfigureAwait(false);

        var response = _responses.Count > 0
            ? _responses[_callIndex % _responses.Count]
            : string.Empty;

        _callIndex++;
        return response;
    }
}
