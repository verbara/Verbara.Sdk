using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Verbara.Sdk.Sessions.FunctionalTests;

public sealed class ReconciliationTests : IAsyncLifetime
{
    private readonly SessionTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ConcurrentEvents_ShouldMaintainConsistentIndices_WhenBurstOf50()
    {
        const int count = 50;
        var tasks = new Task[count];

        for (var i = 0; i < count; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(() =>
            {
                var uid = $"rc-burst-{idx:D3}";
                var linked = $"rc-burst-linked-{idx:D3}";
                _fixture.SimulateNewChannel(uid, $"PJSIP/trunk-{idx:D3}",
                    ChannelState.Ring, linkedId: linked, context: "from-trunk");
            });
        }

        await Task.WhenAll(tasks);

        // All 50 sessions should exist with correct indices
        for (var i = 0; i < count; i++)
        {
            var uid = $"rc-burst-{i:D3}";
            var linked = $"rc-burst-linked-{i:D3}";

            var byChannel = _fixture.SessionManager.GetByChannelId(uid);
            byChannel.Should().NotBeNull($"session for channel {uid} should exist");

            var byLinked = _fixture.SessionManager.GetByLinkedId(linked);
            byLinked.Should().NotBeNull($"session for linkedId {linked} should exist");
            byLinked.Should().BeSameAs(byChannel);
        }

        _fixture.SessionManager.ActiveSessions.Count().Should().BeGreaterOrEqualTo(count);
    }

    // NOTE (2026-09-24 — openspec change a-reconnect-reload-is-a-diff-not-a-wipe, task 1.2):
    // this test exercises no reconnect, and cleans nothing. It never raises
    // IAmiConnection.Reconnected — it detaches and re-attaches the session manager instead — and
    // raising it here would reach no handler either: VerbaraServer subscribes it in StartAsync
    // (src/Verbara.Sdk.Live/Server/VerbaraServer.cs:91, the only subscription site in src/), and
    // SessionTestFixture never calls StartAsync. The assertion below is that the session SURVIVES
    // the simulated reconnect, which is the stranded session that change removes. Left exactly as
    // found on purpose — whichever change repairs this test owns it. The full record, including
    // which reconnection tests could have caught the defect, is kept with the change
    // "a-reconnect-reload-is-a-diff-not-a-wipe" (notes-what-the-existing-suite-never-exercised).
    [Fact]
    public void Reconnection_ShouldCleanSessions_WhenServerReconnects()
    {
        // Create an active session
        _fixture.SimulateNewChannel("rc-recon-1", "PJSIP/trunk-010",
            ChannelState.Ring, linkedId: "rc-linked-5", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("rc-linked-5")!;
        session.State.Should().Be(CallSessionState.Created);

        // Detach simulates what happens on reconnect — manager detaches from server
        _fixture.SessionManager.DetachFromServer("test-srv");

        // After detach, new events won't update sessions
        // The session still exists in the store but won't receive updates
        _fixture.SessionManager.GetByLinkedId("rc-linked-5").Should().NotBeNull();

        // Re-attach
        _fixture.SessionManager.AttachToServer(_fixture.Server, "test-srv");

        // New channels after re-attach should work normally
        _fixture.SimulateNewChannel("rc-recon-2", "PJSIP/trunk-011",
            ChannelState.Ring, linkedId: "rc-linked-6", context: "from-trunk");

        _fixture.SessionManager.GetByLinkedId("rc-linked-6").Should().NotBeNull();
    }
}
