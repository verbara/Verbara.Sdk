using System.Text.Json;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;
using StackExchange.Redis;

var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
Console.WriteLine($"[AOT] Connecting to Redis at {host}:6379...");

try
{
    using var redis = await ConnectionMultiplexer.ConnectAsync($"{host}:6379,connectTimeout=5000");
    var db = redis.GetDatabase();
    Console.WriteLine("[AOT] Connected.");

    // 1. StringSet/StringGet
    await db.StringSetAsync("aot:test:string", "hello-aot");
    var val = await db.StringGetAsync("aot:test:string");
    Console.WriteLine($"[AOT] StringGet: {val}");

    // 2. HashSet/HashGetAll
    await db.HashSetAsync("aot:test:hash", [
        new HashEntry("field1", "value1"),
        new HashEntry("field2", "42"),
    ]);
    var hash = await db.HashGetAllAsync("aot:test:hash");
    Console.WriteLine($"[AOT] HashGetAll: {hash.Length} entries");

    // 3. JSON Serialize/Deserialize with source gen
    var snapshot = new AotSessionSnapshot
    {
        SessionId = "aot-001",
        State = CallSessionState.Connected,
        Direction = CallDirection.Inbound,
        HangupCause = HangupCause.NormalClearing,
        Participants = [new SessionParticipant
        {
            UniqueId = "chan-001", Channel = "SIP/test", Technology = "SIP",
            Role = ParticipantRole.Caller,
        }],
        Events = [new CallSessionEvent(DateTimeOffset.UtcNow, CallSessionEventType.Created, null, null, "test")],
        Metadata = new() { ["key"] = "value" },
    };

    var json = JsonSerializer.Serialize(snapshot, AotJsonContext.Default.AotSessionSnapshot);
    Console.WriteLine($"[AOT] Serialized: {json.Length} chars");

    var deserialized = JsonSerializer.Deserialize(json, AotJsonContext.Default.AotSessionSnapshot);
    Console.WriteLine($"[AOT] Deserialized: SessionId={deserialized?.SessionId}, State={deserialized?.State}");

    // 4. Store JSON in Redis
    await db.StringSetAsync("aot:test:session", json);
    var loaded = await db.StringGetAsync("aot:test:session");
    var fromRedis = JsonSerializer.Deserialize(loaded.ToString(), AotJsonContext.Default.AotSessionSnapshot);
    Console.WriteLine($"[AOT] From Redis: SessionId={fromRedis?.SessionId}");

    // 5. Pub/Sub
    var sub = redis.GetSubscriber();
    var tcs = new TaskCompletionSource<string>();
    await sub.SubscribeAsync(RedisChannel.Literal("aot:test:channel"), (_, message) =>
    {
        tcs.TrySetResult(message.ToString());
    });
    await sub.PublishAsync(RedisChannel.Literal("aot:test:channel"), "hello-pubsub");
    var pubsubResult = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"[AOT] Pub/Sub: {pubsubResult}");

    // Cleanup
    await db.KeyDeleteAsync(["aot:test:string", "aot:test:hash", "aot:test:session"]);

    Console.WriteLine("[AOT] All checks passed!");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[AOT] FAILED: {ex.Message}");
    return 1;
}
