using System.Globalization;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using AmiConnectionFactory = Verbara.Sdk.FunctionalTests.Infrastructure.Helpers.AmiConnectionFactory;
using Verbara.Sdk.TestInfrastructure;
using Verbara.Sdk.TestInfrastructure.Stacks;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>
/// The xUnit class fixture over <see cref="MultiServerFixture"/>: one two-server run for the class, and the one class
/// measurement its class tests share (N queue calls observed by every AMI user class).
/// </summary>
public sealed class MultiServerTestFixture : Xunit.IAsyncLifetime
{
    /// <summary>The queue calls of the class measurement.</summary>
    public const int Calls = 10;

    /// <summary>A hang bound for any wait on calls; the calls themselves end within seconds.</summary>
    public static readonly TimeSpan CallBound = TimeSpan.FromSeconds(90);

    private readonly Lock _measurementGate = new();
    private Task<ClassMeasurement>? _measurement;

    public MultiServerTestFixture()
    {
        Lab = new MultiServerFixture();
    }

    public MultiServerFixture Lab { get; }

    /// <summary>The Asterisk version the leg's pinned base carries, such as <c>22.10.1</c>, read from the base's build tag.</summary>
    public static string PinnedVersion
    {
        get
        {
            var reference = AsteriskBaseImages.Resolve(Environment.GetEnvironmentVariable("ASTERISK_VERSION") ?? "22");
            var tag = reference[(reference.IndexOf(':', StringComparison.Ordinal) + 1)..];
            return tag[..tag.IndexOf('_', StringComparison.Ordinal)];
        }
    }

    /// <summary>The class measurement, taken once by the first test that asks for it.</summary>
    public Task<ClassMeasurement> MeasurementAsync()
    {
        lock (_measurementGate)
            return _measurement ??= MeasureClassesAsync();
    }

    public Task InitializeAsync() => Lab.InitializeAsync();

    public Task DisposeAsync() => Lab.DisposeAsync();

    /// <summary>An SDK AMI connection to server A or B as <paramref name="user"/>, not yet connected.</summary>
    public AmiConnection Connect(char server, AmiUserClass user, string? secret = null) =>
        AmiConnectionFactory.Create(server == 'A' ? Lab.ServerAAddress : Lab.ServerBAddress, MultiServerAmiUsers.AmiPort,
            configure: o =>
            {
                o.Username = user.Name;
                o.Password = secret ?? (server == 'A' ? Lab.SecretA(user) : Lab.SecretB(user));
                o.AutoReconnect = false;
            });

    /// <summary>
    /// Sends the marker call on A and waits until every tally in <paramref name="drained"/> has received its
    /// <c>UserEvent</c>: AMI delivers a session's events in order, so each has then received everything A sent it
    /// before the marker.
    /// </summary>
    internal static async Task DrainAsync(IAmiConnection driver, IReadOnlyList<AmiEventTally> drained)
    {
        var marker = "LabEnd" + Guid.NewGuid().ToString("N")[..8];
        foreach (var tally in drained)
            tally.ExpectMarker(marker);

        await driver.SendActionAsync(new OriginateAction
        {
            Channel = $"Local/marker@{MultiServerFixture.LabContext}/n",
            Application = "UserEvent",
            Data = marker,
            IsAsync = true,
        });

        foreach (var tally in drained)
            await tally.WaitUntilAsync(t => t.Drained, CallBound, $"{tally.Label} receiving the marker {marker}");
    }

    private async Task<ClassMeasurement> MeasureClassesAsync()
    {
        var connections = new List<AmiConnection>();
        var tallies = new List<AmiEventTally>();
        try
        {
            async Task<AmiEventTally> ObserveAsync(char server, AmiUserClass user)
            {
                var connection = Connect(server, user);
                connections.Add(connection);
                var tally = new AmiEventTally($"{server}:{user.Name}", connection);
                tallies.Add(tally);
                await connection.ConnectAsync();
                return tally;
            }

            var observers = new Dictionary<string, AmiEventTally>(StringComparer.Ordinal);
            foreach (var user in MultiServerAmiUsers.All)
                observers[user.Name] = await ObserveAsync('A', user);
            var farEnd = await ObserveAsync('B', MultiServerAmiUsers.Full);

            var driver = Connect('A', MultiServerAmiUsers.Full);
            connections.Add(driver);
            await driver.ConnectAsync();

            for (var i = 0; i < Calls; i++)
            {
                var response = await driver.SendActionAsync(new OriginateAction
                {
                    Channel = $"Local/queue@{MultiServerFixture.LabContext}/n",
                    Application = "Wait",
                    Data = "3",
                    IsAsync = true,
                    ActionId = string.Create(CultureInfo.InvariantCulture, $"queue-call-{i}"),
                });
                if (!string.Equals(response.Response, "Success", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"originate {i} was refused: {response.Response} {response.Message}");
            }

            // Fence 1: the ground truth is complete. Each queue call ends three legs on A (the two Local halves and the
            // trunk leg) and one on B.
            var full = observers[MultiServerAmiUsers.Full.Name];
            await full.WaitUntilAsync(t => t.Count("Hangup") >= 3 * Calls, CallBound, $"A's {3 * Calls} Hangup events");
            await farEnd.WaitUntilAsync(t => t.Count("Hangup") >= Calls, CallBound, $"B's {Calls} Hangup events");

            // Fence 2: every observer on A has drained what A sent it before the marker.
            await DrainAsync(driver, [.. observers.Values]);

            return new ClassMeasurement(
                observers.ToDictionary(o => o.Key, o => Snapshot(o.Value), StringComparer.Ordinal),
                Snapshot(farEnd),
                string.Join(Environment.NewLine, tallies.Select(t => t.Describe())));
        }
        finally
        {
            tallies.ForEach(t => t.Dispose());
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    private static Dictionary<string, int> Snapshot(AmiEventTally tally) =>
        ClassMeasurement.EventNames.ToDictionary(name => name, tally.Count, StringComparer.Ordinal);
}

/// <summary>What each AMI user on A, and the <c>full</c> user on B, counted over the class measurement's calls.</summary>
public sealed record ClassMeasurement(
    IReadOnlyDictionary<string, Dictionary<string, int>> ServerA,
    IReadOnlyDictionary<string, int> ServerB,
    string Description)
{
    /// <summary>The events compared.</summary>
    public static readonly IReadOnlyList<string> EventNames =
        ["Newchannel", "Hangup", "AgentConnect", "AgentCalled", "QueueCallerJoin", "Newexten", "VarSet"];

    /// <summary>The count of <paramref name="eventName"/> that <paramref name="user"/> received on A.</summary>
    public int A(string user, string eventName) => ServerA[user][eventName];
}
