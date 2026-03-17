using Asterisk.Sdk.Sessions.Manager;

namespace Asterisk.Sdk.Sessions.Internal;

internal sealed class SessionCorrelator
{
    private readonly SessionOptions _options;

    public SessionCorrelator(SessionOptions options) => _options = options;

    public CallDirection InferDirection(string? context, string? extension)
    {
        if (context is null) return CallDirection.Inbound;

        foreach (var pattern in _options.OutboundContextPatterns)
            if (context.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return CallDirection.Outbound;

        foreach (var pattern in _options.InboundContextPatterns)
            if (context.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return CallDirection.Inbound;

        return CallDirection.Inbound; // Default
    }

    public static bool IsLocalChannel(string channelName) =>
        channelName.StartsWith("Local/", StringComparison.OrdinalIgnoreCase);

    public static string ExtractTechnology(string channelName)
    {
        var slashIndex = channelName.IndexOf('/');
        return slashIndex > 0 ? channelName[..slashIndex] : "Unknown";
    }

    public static ParticipantRole InferRole(string channelName, int participantCount) =>
        IsLocalChannel(channelName) ? ParticipantRole.Internal
        : participantCount == 0 ? ParticipantRole.Caller
        : ParticipantRole.Destination;
}
