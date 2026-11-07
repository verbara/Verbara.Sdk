using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class ExistingCallAdmissionTests
{
    [Fact]
    public async Task AdmitExistingCalls_ShouldAdmitHeldCallsOnce_WhenTheAttachedTableIsAlreadyComplete()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        rig.Add("X"); rig.Add("Y"); rig.Add("Z", "K");
        rig.Attach();
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.Run.FirstPeer.StatusChannels = [new StatusChannel("X", "PJSIP/X", "L"),
            new StatusChannel("Y", "PJSIP/Y", "L"), new StatusChannel("Z", "PJSIP/Z", "K")];
        var asks = rig.Run.FirstPeer.Asked("Status");
        await rig.Manager.AdmitExistingCalls("server");
        rig.Manager.ActiveSessions.Should().HaveCount(2);
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(call);
        rig.Manager.GetByChannelId("server", "Y").Should().BeSameAs(call);
        call.State.Should().Be(CallSessionState.Connected);
        call.Metadata["origin"].Should().Be("reload");
        rig.Events.OfType<CallStartedEvent>().Should().HaveCount(2);
        rig.Run.FirstPeer.Asked("Status").Should().Be(asks);
        var saved = rig.Store.Saves.Count;
        var events = call.Events.Count;
        await rig.Manager.AdmitExistingCalls("server");
        rig.Store.Saves.Count.Should().Be(saved);
        call.Events.Count.Should().Be(events);
        rig.Events.OfType<CallStartedEvent>().Should().HaveCount(2);
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldJoinTheReconstructedCallWithoutStarting_WhenItWasRegisteredBeforeAttach()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        var call = new CallSession("restored", "L", "server", CallDirection.Inbound);
        rig.Manager.RegisterReconstructedSession(call).Should().BeTrue();
        rig.Add("X"); rig.Add("Y"); rig.Attach();
        call.Participants.Should().BeEmpty();
        await rig.Manager.AdmitExistingCalls("server");
        call.Participants.Should().HaveCount(2);
        rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(call);
        rig.Manager.GetByChannelId("server", "Y").Should().BeSameAs(call);
        rig.Events.OfType<CallStartedEvent>().Should().BeEmpty();
        rig.Hangup("X"); rig.Hangup("Y", HangupCause.UserBusy);
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.UserBusy);
        call.Metadata.Should().NotContainKey("cause");
        rig.AssertIndexes();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldRefuse_WhenTheIdIsNotAttached()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        var error = await Record.ExceptionAsync(() => rig.Manager.AdmitExistingCalls("n9").AsTask());
        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("n9");
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.Events.Should().BeEmpty();
        rig.Store.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldAdmitNothing_WhenAnImplementationReliesOnTheDefault()
    {
        await using ICallSessionManager manager = new DefaultManager();
        await manager.AdmitExistingCalls("unattached");
        manager.ActiveSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task AdmitExistingCalls_ShouldDecideThePendingLeftCall_WhenItsCompleteReplacementHasNoHeldChannel()
    {
        await using var rig = await AdmissionRig.CreateAsync();
        await using var replacement = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true });
        rig.Attach(); rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.State = CallSessionState.Connected;
        using var gate = new NotificationGate();
        using var subscription = rig.Manager.Events.Subscribe(new AdmissionObserver(e => { if (e is CallHeldEvent) gate.Hold(); }));
        var workers = new List<Task>();
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Run.Server.Channels.OnHold("X", null)));
            await gate.Entered.Task.WaitAsync(Run.Bound);
            rig.Manager.DetachFromServer("server");
            var attach = Task.Run(() => rig.Manager.AttachToServer(replacement.Server, "server"));
            workers.Add(attach);
            await attach.WaitAsync(Run.Bound);
            rig.Log.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains(call.SessionId, StringComparison.Ordinal)).Should().Be(1);
            gate.Release();
            await Task.WhenAll(workers).WaitAsync(Run.Bound);
        });
        rig.Manager.IsPendingDecision(call).Should().BeTrue();
        var asks = replacement.FirstPeer.Asked("Status");
        await rig.Manager.AdmitExistingCalls("server");
        rig.Ends(call).Should().Be(1);
        call.Metadata["cause"].Should().Be("reload");
        call.HangupCause.Should().BeNull();
        call.Participants.Should().OnlyContain(p => p.LeftAt.HasValue);
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft && e.Detail == "reload").Should().Be(1);
        rig.Manager.GetByChannelId("server", "X").Should().BeNull();
        replacement.FirstPeer.Asked("Status").Should().Be(asks);
        rig.Store.Saves.Last(s => s.Id == call.SessionId).Present.Should().Be(0);
        await rig.Manager.AdmitExistingCalls("server");
        rig.Ends(call).Should().Be(1);
        rig.AssertIndexes();
    }

    private sealed class DefaultManager : ICallSessionManager
    {
        public CallSession? GetById(string sessionId) => null;
        public CallSession? GetByLinkedId(string linkedId) => null;
        public CallSession? GetByChannelId(string uniqueId) => null;
        public CallSession? GetByBridgeId(string bridgeId) => null;
        public IEnumerable<CallSession> ActiveSessions => [];
        public IEnumerable<CallSession> GetRecentCompleted(int count = 100) => [];
        public IObservable<SessionDomainEvent> Events => throw new NotSupportedException();
        public void AttachToServer(VerbaraServer server, string serverId) { }
        public void DetachFromServer(string serverId) { }
        public bool RegisterReconstructedSession(CallSession session) => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class AdmissionRig : IAsyncDisposable
{
    public Run Run { get; }
    public AdmissionStore Store { get; } = new();
    public AdmissionLog Log { get; } = new();
    public CallSessionManager Manager { get; }
    public ConcurrentQueue<SessionDomainEvent> Events { get; } = new();
    private readonly IDisposable _subscription;
    private AdmissionRig(Run run)
    {
        Run = run;
        Manager = new CallSessionManager(Options.Create(new SessionOptions()), Log, Store);
        _subscription = Manager.Events.Subscribe(new AdmissionObserver(Events.Enqueue));
    }
    public static async Task<AdmissionRig> CreateAsync(bool complete = true, BootingAsterisk? peer = null) => new(complete
        ? await Run.StartAsync(peer ?? new BootingAsterisk { BootedAtLogin = true })
        : await Run.ConnectAsync(peer ?? new BootingAsterisk { BootedAtLogin = true }));
    public void Attach() => Manager.AttachToServer(Run.Server, "server");
    public void Add(string id, string linked = "L") => Run.Server.Channels.OnNewChannel(id, "PJSIP/" + id, ChannelState.Up, linkedId: linked);
    public void Hangup(string id, HangupCause cause = HangupCause.NormalClearing) => Run.Server.Channels.OnHangup(id, cause);
    public int Ends(CallSession call) => Events.OfType<CallEndedEvent>().Count(e => e.SessionId == call.SessionId);
    public void AssertIndexes() => SessionIndexInvariant.Assert(Manager);
    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await Manager.DisposeAsync();
        await Run.DisposeAsync();
    }
}
internal sealed class AdmissionObserver(Action<SessionDomainEvent> action) : IObserver<SessionDomainEvent>
{
    public void OnNext(SessionDomainEvent value) => action(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
internal sealed class AdmissionStore : SessionStoreBase
{
    public ConcurrentQueue<(string Id, CallSessionState State, int Present)> Saves { get; } = new();
    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        Saves.Enqueue((session.SessionId, session.State, session.Participants.Count(p => !p.LeftAt.HasValue)));
        return ValueTask.CompletedTask;
    }
    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
}
internal sealed class AdmissionLog : ILogger<CallSessionManager>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}
