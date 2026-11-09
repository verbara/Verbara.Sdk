using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class AdmissionRetentionActivityTests
{
    [Fact]
    public async Task AdmitExistingCalls_ShouldDriveExpiredCallRetention_WhenItAdmitsAHeldChannel()
    {
        await using var run = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true });
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var manager = new CallSessionManager(
            Options.Create(new SessionOptions { CompletedRetention = TimeSpan.FromSeconds(1) }),
            new AdmissionLog(), new AdmissionStore(), clock);
        var notifications = new ConcurrentQueue<SessionDomainEvent>();
        using var subscription = manager.Events.Subscribe(new AdmissionObserver(notifications.Enqueue));
        manager.AttachToServer(run.Server, "server");
        run.Server.Channels.OnNewChannel("expired-X", "PJSIP/expired-X", ChannelState.Up, linkedId: "expired-L");
        var old = manager.GetByLinkedId("server", "expired-L")!;
        run.Server.Channels.OnHangup("expired-X", HangupCause.NormalClearing);
        old.EndingDelivered.Should().BeTrue();
        manager.GetById(old.SessionId).Should().BeSameAs(old);
        manager.DetachFromServer("server", DetachedCalls.Keep);
        run.Server.Channels.OnNewChannel("admit-X", "PJSIP/admit-X", ChannelState.Up, linkedId: "admit-L");
        clock.Advance(old.CompletedAt!.Value + TimeSpan.FromSeconds(2) - clock.GetUtcNow());
        manager.AttachToServer(run.Server, "server");
        run.Server.ChannelTableComplete.Should().BeTrue();
        manager.GetById(old.SessionId).Should().BeSameAs(old, "attach itself did not run an arrival/retention walk");
        manager.GetByLinkedId("server", "admit-L").Should().BeNull("the new held channel has no session yet");
        var asks = run.FirstPeer.Asked("Status");
        await manager.AdmitExistingCalls("server");
        manager.GetById(old.SessionId).Should().BeNull("the real admission, not a direct helper call, drives retention activity");
        manager.GetByLinkedId("server", "expired-L").Should().BeNull();
        var admitted = manager.GetByLinkedId("server", "admit-L");
        admitted.Should().NotBeNull();
        manager.GetByChannelId("server", "admit-X").Should().BeSameAs(admitted);
        notifications.OfType<CallStartedEvent>().Count(e => e.SessionId == admitted!.SessionId).Should().Be(1);
        run.FirstPeer.Asked("Status").Should().Be(asks);
        SessionIndexInvariant.Assert(manager);
    }
}
