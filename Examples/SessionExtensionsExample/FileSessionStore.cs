// Asterisk.Sdk - Custom SessionStoreBase implementation
// Demonstrates: persisting sessions as JSON lines to a file with an in-memory cache.

using System.Text.Json;
using System.Text.Json.Serialization;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;

namespace SessionExtensionsExample;

/// <summary>
/// Serializable snapshot of a <see cref="CallSession"/> for JSON persistence.
/// CallSession itself contains internal fields (Lock, ConcurrentDictionary) that
/// are not directly serializable, so we project into this DTO.
/// </summary>
internal sealed class CallSessionDto
{
    public required string SessionId { get; init; }
    public required string LinkedId { get; init; }
    public required string ServerId { get; init; }
    public required CallDirection Direction { get; init; }
    public required CallSessionState State { get; init; }
    public string? QueueName { get; init; }
    public string? AgentId { get; init; }
    public string? AgentInterface { get; init; }
    public string? BridgeId { get; init; }
    public HangupCause? HangupCause { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; init; }
    public DateTimeOffset? RingingAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int ParticipantCount { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }

    public static CallSessionDto FromSession(CallSession session)
    {
        return new CallSessionDto
        {
            SessionId = session.SessionId,
            LinkedId = session.LinkedId,
            ServerId = session.ServerId,
            Direction = session.Direction,
            State = session.State,
            QueueName = session.QueueName,
            AgentId = session.AgentId,
            AgentInterface = session.AgentInterface,
            BridgeId = session.BridgeId,
            HangupCause = session.HangupCause,
            CreatedAt = session.CreatedAt,
            DialingAt = session.DialingAt,
            RingingAt = session.RingingAt,
            ConnectedAt = session.ConnectedAt,
            CompletedAt = session.CompletedAt,
            ParticipantCount = session.Participants.Count,
            Metadata = session.Metadata.Count > 0
                ? new Dictionary<string, string>(session.Metadata)
                : null
        };
    }
}

/// <summary>
/// AOT-safe JSON serializer context for the file session store.
/// </summary>
[JsonSerializable(typeof(CallSessionDto))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class FileStoreJsonContext : JsonSerializerContext;

/// <summary>
/// A custom <see cref="SessionStoreBase"/> that persists session snapshots as JSON lines (.jsonl).
/// Uses a <see cref="Dictionary{TKey,TValue}"/> as an in-memory cache protected by <see cref="Lock"/>.
/// </summary>
internal sealed class FileSessionStore : SessionStoreBase
{
    private readonly string _filePath;
    private readonly Dictionary<string, CallSession> _cache = [];
    private readonly Lock _lock = new();

    public FileSessionStore(string filePath)
    {
        _filePath = filePath;
    }

    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        lock (_lock)
        {
            _cache[session.SessionId] = session;
        }

        FlushToFile();
        return ValueTask.CompletedTask;
    }

    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        lock (_lock)
        {
            _cache.TryGetValue(sessionId, out var session);
            return ValueTask.FromResult(session);
        }
    }

    public override ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var active = _cache.Values
                .Where(s => s.State is not (CallSessionState.Completed
                    or CallSessionState.Failed
                    or CallSessionState.TimedOut))
                .ToList();

            return ValueTask.FromResult<IEnumerable<CallSession>>(active);
        }
    }

    public override ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        lock (_lock)
        {
            _cache.Remove(sessionId);
        }

        FlushToFile();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Print a summary of all cached sessions to the console.
    /// </summary>
    public void PrintSummary()
    {
        lock (_lock)
        {
            Console.WriteLine();
            Console.WriteLine($"File Session Store Summary ({_filePath})");
            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"Total sessions: {_cache.Count}");

            var byState = _cache.Values
                .GroupBy(s => s.State)
                .OrderBy(g => g.Key);

            foreach (var group in byState)
            {
                Console.WriteLine($"  {group.Key}: {group.Count()}");
            }

            Console.WriteLine();

            foreach (var session in _cache.Values.OrderByDescending(s => s.CreatedAt).Take(10))
            {
                Console.WriteLine($"  [{session.SessionId[..8]}] {session.State,-12} " +
                    $"{session.Direction,-9} Duration: {session.Duration.TotalSeconds:F1}s " +
                    $"Agent: {session.AgentId ?? "-"}");
            }

            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"Sessions persisted to: {Path.GetFullPath(_filePath)}");
        }
    }

    private void FlushToFile()
    {
        lock (_lock)
        {
            using var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);

            foreach (var session in _cache.Values)
            {
                var dto = CallSessionDto.FromSession(session);
                var json = JsonSerializer.Serialize(dto, FileStoreJsonContext.Default.CallSessionDto);
                writer.WriteLine(json);
            }
        }
    }
}
