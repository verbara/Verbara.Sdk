using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

public sealed class CallSessionManagerLookupTests
{
    private const string ServerA = "server-A";
    private const string ServerB = "server-B";
    private const string ServerC = "server-C";
    private const string SharedLinkedId = "shared-linked";
    private const string SharedLegId = "shared-leg";
    private const string SharedBridgeId = "shared-bridge";

    [Theory]
    [InlineData("linked")]
    [InlineData("channel")]
    [InlineData("bridge")]
    public async Task GetByScopedId_ShouldReturnOnlyTheNamedServer_WhenTheLegacyLookupReturnsACall(string kind)
    {
        var call = NewCall("legacy", ServerA, SharedLinkedId, SharedLegId, SharedBridgeId);
        await using ICallSessionManager legacy = new LegacyManager(call);
        var id = IdFor(kind, SharedLinkedId, SharedLegId, SharedBridgeId);

        using var scope = new AssertionScope();
        Unscoped(legacy, kind, id).Should().BeSameAs(call, "premise: the old lookup has a known answer");
        Scoped(legacy, kind, ServerA, id).Should().BeSameAs(call,
            "the scoped default must return the matching server's known call");
        Scoped(legacy, kind, ServerB, id).Should().BeNull(
            "the scoped default must reject a different server");
        Scoped(legacy, kind, "server-a", id).Should().BeNull("server ids are ordinal");
        Scoped(legacy, kind, ServerA, "unknown").Should().BeNull("an unknown old lookup has no answer");
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("channel")]
    [InlineData("bridge")]
    public async Task GetByScopedId_ShouldReturnNull_WhenTheLegacyLookupHasNoAnswer(string kind)
    {
        await using ICallSessionManager legacy = new LegacyManager(null);

        using var scope = new AssertionScope();
        Unscoped(legacy, kind, "unknown").Should().BeNull("premise: the old lookup has no answer");
        Scoped(legacy, kind, ServerA, "unknown").Should().BeNull();
        Scoped(legacy, kind, ServerB, "unknown").Should().BeNull();
        Scoped(legacy, kind, "server-a", "unknown").Should().BeNull();
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("channel")]
    [InlineData("bridge")]
    public async Task GetByScopedId_ShouldFindEachCall_WhenRegisteredUnattachedServersShareIds(string kind)
    {
        await using var manager = NewManager();
        ICallSessionManager throughInterface = manager;
        var a = NewCall("session-a", ServerA, SharedLinkedId, SharedLegId, SharedBridgeId);
        var b = NewCall("session-b", ServerB, SharedLinkedId, SharedLegId, SharedBridgeId);
        manager.RegisterReconstructedSession(a).Should().BeTrue("premise: A is registered without an attached oracle");
        manager.RegisterReconstructedSession(b).Should().BeTrue("premise: B has its own server's index");
        var id = IdFor(kind, SharedLinkedId, SharedLegId, SharedBridgeId);

        using var scope = new AssertionScope();
        ConcreteScoped(manager, kind, ServerA, id).Should().BeSameAs(a);
        ConcreteScoped(manager, kind, ServerB, id).Should().BeSameAs(b);
        Scoped(throughInterface, kind, ServerA, id).Should().BeSameAs(a,
            "interface dispatch must reach the concrete scoped implementation");
        Scoped(throughInterface, kind, ServerB, id).Should().BeSameAs(b);
        ConcreteScoped(manager, kind, "missing", id).Should().BeNull();
        ConcreteScoped(manager, kind, "server-a", id).Should().BeNull();
        Scoped(throughInterface, kind, "missing", id).Should().BeNull();
        Scoped(throughInterface, kind, "server-a", id).Should().BeNull();
        ConcreteUnscoped(manager, kind, id).Should().BeNull("registered server ids count toward ambiguity");
        Unscoped(throughInterface, kind, id).Should().BeNull();
        Scoped(throughInterface, kind, ServerA, "unknown").Should().BeNull();
        manager.GetById(a.SessionId).Should().BeSameAs(a);
        manager.GetById(b.SessionId).Should().BeSameAs(b);
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("channel")]
    [InlineData("bridge")]
    public async Task GetByScopedId_ShouldKeepTheUniqueAnswer_WhenOnlyOneUnattachedServerIsRegistered(string kind)
    {
        await using var manager = NewManager();
        ICallSessionManager throughInterface = manager;
        var call = NewCall("sole-session", ServerA, "sole-linked", "sole-leg", "sole-bridge");
        manager.RegisterReconstructedSession(call).Should().BeTrue("premise: the server is registered, not attached");
        var id = IdFor(kind, "sole-linked", "sole-leg", "sole-bridge");

        using var scope = new AssertionScope();
        ConcreteUnscoped(manager, kind, id).Should().BeSameAs(call);
        Unscoped(throughInterface, kind, id).Should().BeSameAs(call);
        ConcreteScoped(manager, kind, ServerA, id).Should().BeSameAs(call);
        Scoped(throughInterface, kind, ServerA, id).Should().BeSameAs(call);
        ConcreteScoped(manager, kind, ServerB, id).Should().BeNull();
        Scoped(throughInterface, kind, ServerB, id).Should().BeNull();
        ConcreteScoped(manager, kind, "server-a", id).Should().BeNull();
        Scoped(throughInterface, kind, "server-a", id).Should().BeNull();
        ConcreteUnscoped(manager, kind, "unknown").Should().BeNull();
        Unscoped(throughInterface, kind, "unknown").Should().BeNull();
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("channel")]
    [InlineData("bridge")]
    public async Task GetByScopedId_ShouldExposeTheDefaultLimit_WhenAnUnscopedAnswerIsAmbiguous(string kind)
    {
        await using var manager = NewManager();
        var a = NewCall("session-a", ServerA, SharedLinkedId, SharedLegId, SharedBridgeId);
        var b = NewCall("session-b", ServerB, SharedLinkedId, SharedLegId, SharedBridgeId);
        var sole = NewCall("sole-session", ServerC, "sole-linked", "sole-leg", "sole-bridge");
        manager.RegisterReconstructedSession(a).Should().BeTrue();
        manager.RegisterReconstructedSession(b).Should().BeTrue();
        manager.RegisterReconstructedSession(sole).Should().BeTrue();
        await using ICallSessionManager legacy = new LegacyManager(null, manager);
        var sharedId = IdFor(kind, SharedLinkedId, SharedLegId, SharedBridgeId);
        var soleId = IdFor(kind, "sole-linked", "sole-leg", "sole-bridge");

        using var scope = new AssertionScope();
        ConcreteScoped(manager, kind, ServerA, sharedId).Should().BeSameAs(a);
        ConcreteScoped(manager, kind, ServerB, sharedId).Should().BeSameAs(b);
        Unscoped(legacy, kind, sharedId).Should().BeNull("premise: the wrapper sees the manager's ambiguous answer");
        Scoped(legacy, kind, ServerA, sharedId).Should().BeNull("the default has only the unscoped answer");
        Scoped(legacy, kind, ServerB, sharedId).Should().BeNull("the default cannot inspect the engine's indexes");
        Unscoped(legacy, kind, soleId).Should().BeSameAs(sole);
        Scoped(legacy, kind, ServerC, soleId).Should().BeSameAs(sole);
        Scoped(legacy, kind, ServerA, soleId).Should().BeNull();
        Scoped(legacy, kind, "server-c", soleId).Should().BeNull();
        Scoped(legacy, kind, ServerC, "unknown").Should().BeNull();
    }

    private static CallSessionManager NewManager() => new(Options.Create(new SessionOptions()),
        NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());

    private static CallSession NewCall(string sessionId, string serverId, string linkedId, string legId, string bridgeId)
    {
        var call = new CallSession(sessionId, linkedId, serverId, CallDirection.Inbound) { BridgeId = bridgeId };
        call.AddParticipant(new SessionParticipant
        {
            UniqueId = legId,
            Channel = "PJSIP/lookup-" + sessionId,
            Technology = "PJSIP",
            Role = ParticipantRole.Caller,
        });
        return call;
    }

    private static string IdFor(string kind, string linkedId, string legId, string bridgeId) => kind switch
    {
        "linked" => linkedId,
        "channel" => legId,
        "bridge" => bridgeId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CallSession? Scoped(ICallSessionManager manager, string kind, string serverId, string id) => kind switch
    {
        "linked" => manager.GetByLinkedId(serverId, id),
        "channel" => manager.GetByChannelId(serverId, id),
        "bridge" => manager.GetByBridgeId(serverId, id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CallSession? Unscoped(ICallSessionManager manager, string kind, string id) => kind switch
    {
        "linked" => manager.GetByLinkedId(id),
        "channel" => manager.GetByChannelId(id),
        "bridge" => manager.GetByBridgeId(id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CallSession? ConcreteScoped(CallSessionManager manager, string kind, string serverId, string id) => kind switch
    {
        "linked" => manager.GetByLinkedId(serverId, id),
        "channel" => manager.GetByChannelId(serverId, id),
        "bridge" => manager.GetByBridgeId(serverId, id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CallSession? ConcreteUnscoped(CallSessionManager manager, string kind, string id) => kind switch
    {
        "linked" => manager.GetByLinkedId(id),
        "channel" => manager.GetByChannelId(id),
        "bridge" => manager.GetByBridgeId(id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // Implements only the abstract surface published in 2.8.0. Scoped calls use interface defaults.
    private sealed class LegacyManager(CallSession? known, ICallSessionManager? source = null) : ICallSessionManager
    {
        public IEnumerable<CallSession> ActiveSessions => [];
        public IObservable<SessionDomainEvent> Events => Observable.Empty<SessionDomainEvent>();
        public CallSession? GetById(string sessionId) => source is not null
            ? source.GetById(sessionId) : known?.SessionId == sessionId ? known : null;
        public CallSession? GetByLinkedId(string linkedId) => source is not null
            ? source.GetByLinkedId(linkedId) : known?.LinkedId == linkedId ? known : null;
        public CallSession? GetByChannelId(string uniqueId) => source is not null
            ? source.GetByChannelId(uniqueId) : uniqueId == SharedLegId ? known : null;
        public CallSession? GetByBridgeId(string bridgeId) => source is not null
            ? source.GetByBridgeId(bridgeId) : known?.BridgeId == bridgeId ? known : null;
        public IEnumerable<CallSession> GetRecentCompleted(int count = 100) => [];
        public void AttachToServer(VerbaraServer server, string serverId) { }
        public void DetachFromServer(string serverId) { }
        public bool RegisterReconstructedSession(CallSession session) => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
