using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;

namespace PbxAdmin.Services;

public static class SessionExtensions
{
    private static string ShortChannel(string ch)
    {
        var dash = ch.LastIndexOf('-');
        return dash > 0 ? ch[..dash] : ch;
    }

    public static CallSession? FindByChannel(
        this ICallSessionManager mgr, string channelName)
    {
        var short_ = ShortChannel(channelName);
        return mgr.ActiveSessions.FirstOrDefault(s =>
            s.AgentInterface?.StartsWith(short_, StringComparison.Ordinal) == true
            || s.Participants.Any(p => p.LeftAt is null
                && ShortChannel(p.Channel).Equals(short_, StringComparison.Ordinal)));
    }

    public static IEnumerable<CallSession> Search(
        this ICallSessionManager mgr, string? query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        return mgr.ActiveSessions.Concat(mgr.GetRecentCompleted(limit))
            .Where(s =>
                s.CallerIdNum?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
                || s.AgentId?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
                || s.QueueName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
                || s.SessionId.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(limit);
    }

    public static SessionParticipant? GetCaller(this CallSession s)
        => s.Participants.FirstOrDefault(p => p.Role == ParticipantRole.Caller)
           ?? (s.Participants.Count > 0 ? s.Participants[0] : null);

    public static SessionParticipant? GetDestination(this CallSession s)
        => s.Participants.FirstOrDefault(p => p.Role == ParticipantRole.Destination);
}
