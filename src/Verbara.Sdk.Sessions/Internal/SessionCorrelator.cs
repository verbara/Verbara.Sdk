using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Internal;

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

    // A snoop is a tap Asterisk creates on a call (app_snoop / ARI snoop), named Snoop/<target uniqueid>-<n>;
    // it is never a call or a party to one. The '/' keeps a technology merely starting with "Snoop" out.
    internal static bool IsSnoopChannel(string channelName) =>
        channelName.StartsWith("Snoop/", StringComparison.OrdinalIgnoreCase);

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
