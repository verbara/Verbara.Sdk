namespace Verbara.Sdk.Push.AspNetCore;

using Microsoft.Extensions.Primitives;
using Verbara.Sdk.Push.Authz;
using Verbara.Sdk.Push.Delivery;
using Verbara.Sdk.Push.Topics;

/// <summary>The kind of answer an SSE stream request gets, decided before the response starts.</summary>
internal enum SseAdmissionOutcome
{
    /// <summary>At least one requested topic is allowed: the stream is served.</summary>
    Admitted,

    /// <summary>A requested topic fails to parse: <c>400 Bad Request</c>, the authorizer is asked nothing.</summary>
    InvalidTopic,

    /// <summary>Nothing requested is allowed (no topic means <c>**</c>): <c>403 Forbidden</c>.</summary>
    Denied,
}

/// <summary>
/// What an SSE stream request is answered: the outcome, the patterns the stream delivers, and the raw
/// topics that were refused (denied or unparseable), exactly as the client requested them.
/// </summary>
internal sealed record SseAdmissionDecision(
    SseAdmissionOutcome Outcome,
    IReadOnlyList<TopicPattern> AllowedPatterns,
    IReadOnlyList<string> RefusedTopics,
    string? FirstDenialReason);

/// <summary>
/// The SSE push stream's admission rule: a pure function of the
/// requested topics, the subscriber and the authorizer. It never widens a request to a pattern the
/// client did not name; a request with no topic is a request for <c>**</c>, authorized like any other.
/// </summary>
internal static class SseAdmission
{
    /// <summary>The pattern a request with no topic (or only blank ones) asks for.</summary>
    internal const string CatchAllTopic = "**";

    /// <summary>
    /// Decides the answer: parse every requested topic first (any failure is a 400 and the authorizer is
    /// asked nothing), then ask the authorizer about each named topic only (none allowed is a 403).
    /// </summary>
    public static SseAdmissionDecision Decide(
        StringValues requestedTopics,
        SubscriberContext subscriber,
        ISubscriptionAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(authorizer);

        var parsed = new List<(string Raw, TopicPattern Pattern)>();
        List<string>? invalid = null;

        foreach (var raw in requestedTopics.OfType<string>().Where(static t => !string.IsNullOrWhiteSpace(t)))
        {
            try
            {
                parsed.Add((raw, TopicPattern.Parse(raw)));
            }
            catch (ArgumentException)
            {
                (invalid ??= []).Add(raw);
            }
        }

        if (invalid is not null)
            return new SseAdmissionDecision(SseAdmissionOutcome.InvalidTopic, [], invalid, FirstDenialReason: null);

        if (parsed.Count == 0)
            parsed.Add((CatchAllTopic, TopicPattern.Parse(CatchAllTopic)));

        var allowed = new List<TopicPattern>(parsed.Count);
        var denied = new List<string>();
        string? firstReason = null;

        foreach (var (raw, pattern) in parsed)
        {
            var result = authorizer.CanSubscribe(subscriber with { RequestedTopicPattern = raw }, pattern);
            if (result.Allowed)
            {
                allowed.Add(pattern);
            }
            else
            {
                denied.Add(raw);
                firstReason ??= result.Reason;
            }
        }

        var outcome = allowed.Count == 0 ? SseAdmissionOutcome.Denied : SseAdmissionOutcome.Admitted;
        return new SseAdmissionDecision(outcome, allowed, denied, firstReason);
    }

    /// <summary>
    /// The refused topics percent-encoded and joined by commas, as <c>X-Push-Denied-Topics</c> and the
    /// 400 body carry them: CR, LF and the comma itself never reach a header or a body raw.
    /// </summary>
    public static string EncodeTopics(IReadOnlyList<string> topics) =>
        string.Join(',', topics.Select(Uri.EscapeDataString));
}
