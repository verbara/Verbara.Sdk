using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class VerbaraServerPoolMembershipTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("status-refused")]
    public async Task Add_ShouldRefuseInvalidIdsBeforeConnecting_WhenAnIdIsUnusable(string id)
    {
        await using var rig = new Rig();
        var notifications = 0;
        rig.Pool.ServerAdded += (_, _) => notifications++;
        rig.Pool.ServerRemoved += (_, _) => notifications++;
        var existing = rig.NewServer();
        var handedIn = () => rig.Pool.AddExistingServer(id, existing);
        var connected = async () => await rig.Pool.AddServerAsync(id, new AmiConnectionOptions());
        handedIn.Should().Throw<ArgumentException>();
        await connected.Should().ThrowAsync<ArgumentException>();
        rig.ConnectionsCreated.Should().Be(0);
        rig.Pool.ServerCount.Should().Be(0);
        notifications.Should().Be(0);
        await existing.Connection.DidNotReceive().DisposeAsync();
        await existing.DisposeAsync();
        await existing.Connection.DisposeAsync();
    }

    [Fact]
    public async Task AddServerAsync_ShouldAnnounceOnlyAfterUsableStart_WhenTheLoadIsGated()
    {
        await using var rig = new Rig(gated: true);
        var added = new List<VerbaraServer>();
        rig.Pool.ServerAdded += (id, server) =>
        {
            rig.Pool.GetServer(id).Should().BeSameAs(server);
            added.Add(server);
        };
        var add = rig.Pool.AddServerAsync("n1", new AmiConnectionOptions()).AsTask();
        try
        {
            await rig.LoadEntered.Task.WaitAsync(Bound);
            rig.Pool.GetServer("n1").Should().NotBeNull();
            added.Should().BeEmpty();
        }
        finally { rig.ReleaseLoad.TrySetResult(); }
        var current = await add.WaitAsync(Bound);
        added.Should().Equal(current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddServerAsync_ShouldAnnounceRemovalExactlyOnceBeforeDisposal_WhenASeenStartFails(bool removeFirst)
    {
        await using var rig = new Rig(gated: true, fail: true);
        var added = 0;
        var removed = new List<VerbaraServer>();
        var disposedAtRemoval = false;
        rig.Pool.ServerAdded += (_, _) => added++;
        rig.Pool.ServerRemoved += (_, server) =>
        {
            disposedAtRemoval = rig.DisposedConnections.Contains(server.Connection);
            removed.Add(server);
        };
        var add = rig.Pool.AddServerAsync("n1", new AmiConnectionOptions()).AsTask();
        await rig.LoadEntered.Task.WaitAsync(Bound);
        var seen = rig.Pool.Servers.Single().Value;
        using var follower = new Follower(rig.Pool);
        follower.Walk(rig.Pool.Servers);
        try
        {
            if (removeFirst) await rig.Pool.RemoveServerAsync("n1");
        }
        finally { rig.ReleaseLoad.TrySetResult(); }
        var failed = async () => await add.WaitAsync(Bound);
        await failed.Should().ThrowAsync<AmiNotConnectedException>();
        removed.Should().Equal(seen);
        added.Should().Be(0);
        disposedAtRemoval.Should().BeFalse();
        await seen.Connection.Received(1).DisposeAsync();
        follower.OwnedCount.Should().Be(0);
        seen.Agents.OnAgentLogin("after-removal");
        follower.LoginCount.Should().Be(0);
    }

    [Fact]
    public async Task AddServerAsync_ShouldNotAnnounceOldInstance_WhenRemovedAndReplacedDuringStart()
    {
        await using var rig = new Rig(gated: true);
        var added = new List<VerbaraServer>();
        var removed = new List<VerbaraServer>();
        rig.Pool.ServerAdded += (_, s) => added.Add(s);
        rig.Pool.ServerRemoved += (_, s) => removed.Add(s);
        var add = rig.Pool.AddServerAsync("n1", new AmiConnectionOptions()).AsTask();
        await rig.LoadEntered.Task.WaitAsync(Bound);
        var old = rig.Pool.GetServer("n1")!;
        await rig.Pool.RemoveServerAsync("n1");
        var replacement = rig.NewServer();
        rig.Pool.AddExistingServer("n1", replacement);
        rig.ReleaseLoad.TrySetResult();
        await add.WaitAsync(Bound);
        added.Should().Equal(replacement);
        removed.Should().Equal(old);
        rig.Pool.GetServer("n1").Should().BeSameAs(replacement);
        await old.Connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Membership_ShouldAnnounceEachDepartureOnce_WhenRemovingMissingAndDisposingTwice()
    {
        await using var rig = new Rig();
        var first = rig.NewServer();
        var second = rig.NewServer();
        var added = new List<VerbaraServer>();
        var removed = new List<VerbaraServer>();
        rig.Pool.ServerAdded += (_, s) => added.Add(s);
        rig.Pool.ServerRemoved += (id, s) =>
        {
            rig.DisposedConnections.Should().NotContain(s.Connection);
            rig.Pool.GetServer(id).Should().BeNull();
            removed.Add(s);
        };
        rig.Pool.AddExistingServer("n1", first);
        rig.Pool.AddExistingServer("n2", second);
        var duplicate = () => rig.Pool.AddExistingServer("n1", second);
        duplicate.Should().Throw<InvalidOperationException>();
        await rig.Pool.RemoveServerAsync("absent");
        await rig.Pool.RemoveServerAsync("n1");
        await rig.Pool.DisposeAsync();
        await rig.Pool.DisposeAsync();
        added.Should().Equal(first, second);
        removed.Should().Equal(first, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_ShouldLogEveryFailingHandlerOnceWithException_WhenSubscribersThrow(bool removal)
    {
        using var logs = new Logs();
        await using var rig = new Rig(logs: logs);
        var server = rig.NewServer();
        var exceptions = new[] { new InvalidOperationException("first"), new InvalidOperationException("second") };
        var seen = 0;
        Action<string, VerbaraServer> first = (_, _) => throw exceptions[0];
        Action<string, VerbaraServer> second = (_, _) => throw exceptions[1];
        Action<string, VerbaraServer> last = (_, s) => { s.Should().BeSameAs(server); seen++; };
        if (removal)
        {
            rig.Pool.AddExistingServer("n1", server);
            rig.Pool.ServerRemoved += first;
            rig.Pool.ServerRemoved += second;
            rig.Pool.ServerRemoved += last;
            await rig.Pool.RemoveServerAsync("n1");
            rig.Pool.GetServer("n1").Should().BeNull();
            await server.Connection.Received(1).DisposeAsync();
        }
        else
        {
            rig.Pool.ServerAdded += first;
            rig.Pool.ServerAdded += second;
            rig.Pool.ServerAdded += last;
            rig.Pool.AddExistingServer("n1", server);
            rig.Pool.GetServer("n1").Should().BeSameAs(server);
        }
        seen.Should().Be(1);
        logs.Entries.Should().HaveCount(2);
        logs.Entries.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Warning);
        logs.Entries.Select(e => e.Exception).Should().Equal(exceptions);
    }

    [Theory]
    [InlineData(false, "remove")]
    [InlineData(false, "dispose")]
    [InlineData(false, "add-existing")]
    [InlineData(false, "add-async")]
    [InlineData(true, "remove")]
    [InlineData(true, "dispose")]
    [InlineData(true, "add-existing")]
    [InlineData(true, "add-async")]
    public async Task Membership_ShouldRefuseReentrantMutationBeforeRoutesChange_WhenTwoSubscribersReceiveIt(bool removal, string operation)
    {
        await using var rig = new Rig();
        var server = rig.NewServer();
        var other = rig.NewServer();
        var candidate = rig.NewServer();
        rig.Pool.AddExistingServer("other", other);
        other.Agents.OnAgentLogin("other-agent");
        if (removal) rig.Pool.AddExistingServer("n1", server);
        Task? attempted = null;
        Exception? synchronous = null;
        var notified = 0;
        var unexpected = 0;
        Action<string, VerbaraServer> first = (_, _) =>
        {
            try
            {
                switch (operation)
                {
                    case "remove": attempted = rig.Pool.RemoveServerAsync(removal ? "other" : "n1").AsTask(); break;
                    case "dispose": attempted = rig.Pool.DisposeAsync().AsTask(); break;
                    case "add-existing": rig.Pool.AddExistingServer("candidate", candidate); break;
                    case "add-async": attempted = rig.Pool.AddServerAsync("candidate", new AmiConnectionOptions()).AsTask(); break;
                }
            }
            catch (InvalidOperationException ex) { synchronous = ex; }
        };
        Action<string, VerbaraServer> second = (id, s) =>
        {
            rig.Pool.GetServer("other").Should().BeSameAs(other);
            rig.Pool.GetServerForAgent("other-agent").Should().BeSameAs(other);
            rig.Pool.GetServer(id).Should().BeSameAs(removal ? null : s);
            notified++;
        };
        if (removal)
        {
            rig.Pool.ServerRemoved += first;
            rig.Pool.ServerRemoved += second;
            rig.Pool.ServerAdded += (_, _) => unexpected++;
            await rig.Pool.RemoveServerAsync("n1");
            rig.Pool.ServerRemoved -= first;
            rig.Pool.ServerRemoved -= second;
        }
        else
        {
            rig.Pool.ServerAdded += first;
            rig.Pool.ServerAdded += second;
            rig.Pool.ServerRemoved += (_, _) => unexpected++;
            rig.Pool.AddExistingServer("n1", server);
            rig.Pool.ServerAdded -= first;
            rig.Pool.ServerAdded -= second;
        }
        if (attempted is not null)
        {
            var act = async () => await attempted;
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        else synchronous.Should().BeOfType<InvalidOperationException>();
        notified.Should().Be(1);
        unexpected.Should().Be(0);
        rig.ConnectionsCreated.Should().Be(0);
        rig.Pool.GetServer("candidate").Should().BeNull();
        await candidate.DisposeAsync();
        await candidate.Connection.DisposeAsync();
    }

    [Fact]
    public async Task Membership_ShouldOrderReplacementAfterRemoval_WhenRemovalHandlerIsGated()
    {
        await using var rig = new Rig();
        var old = rig.NewServer();
        var replacement = rig.NewServer();
        rig.Pool.AddExistingServer("n1", old);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var attempting = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<VerbaraServer>();
        using var follower = new Follower(rig.Pool);
        follower.Walk(rig.Pool.Servers);
        rig.Pool.ServerRemoved += (_, s) => { entered.Set(); release.Wait(Bound).Should().BeTrue(); order.Enqueue(s); };
        rig.Pool.ServerAdded += (_, s) => order.Enqueue(s);
        var removal = Task.Run(async () => await rig.Pool.RemoveServerAsync("n1"));
        Task? addition = null;
        try
        {
            entered.Wait(Bound).Should().BeTrue();
            addition = Task.Run(() => { attempting.TrySetResult(Thread.CurrentThread); rig.Pool.AddExistingServer("n1", replacement); });
            var caller = await attempting.Task.WaitAsync(Bound);
            AssertWaitingOnMembership(addition, caller);
            rig.Pool.GetServer("n1").Should().BeNull();
        }
        finally
        {
            release.Set();
            await removal.WaitAsync(Bound);
            if (addition is not null) await addition.WaitAsync(Bound);
        }
        order.Should().Equal(old, replacement);
        follower.OwnedCount.Should().Be(1);
        old.Agents.OnAgentLogin("old");
        replacement.Agents.OnAgentLogin("new");
        follower.LoginCount.Should().Be(1);
    }

    [Fact]
    public async Task AddServerAsync_ShouldFinishAddedBeforeRemoved_WhenRemovalRacesTheSuccessfulStart()
    {
        await using var rig = new Rig(gated: true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        var attempting = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Pool.ServerAdded += (_, _) =>
        {
            entered.Set();
            release.Wait(Bound).Should().BeTrue();
            order.Enqueue("added");
        };
        rig.Pool.ServerRemoved += (_, _) => order.Enqueue("removed");
        var add = Task.Run(async () => await rig.Pool.AddServerAsync("n1", new AmiConnectionOptions()));
        await rig.LoadEntered.Task.WaitAsync(Bound);
        rig.ReleaseLoad.TrySetResult();
        Task? removal = null;
        try
        {
            entered.Wait(Bound).Should().BeTrue();
            removal = Task.Run(async () =>
            {
                attempting.TrySetResult(Thread.CurrentThread);
                await rig.Pool.RemoveServerAsync("n1");
            });
            var caller = await attempting.Task.WaitAsync(Bound);
            AssertWaitingOnMembership(removal, caller);
            rig.Pool.GetServer("n1").Should().NotBeNull();
        }
        finally
        {
            release.Set();
            await add.WaitAsync(Bound);
            if (removal is not null) await removal.WaitAsync(Bound);
        }
        order.Should().Equal("added", "removed");
    }

    [Fact]
    public async Task RemoveServerAsync_ShouldReleaseOutsideMembershipLock_WhenDisposalCallsThePool()
    {
        await using var rig = new Rig();
        var old = rig.NewServer();
        var replacement = rig.NewServer();
        rig.Pool.AddExistingServer("n1", old);
#pragma warning disable CA2012 // NSubstitute setup evaluates the ValueTask.
        old.Connection.DisposeAsync().Returns(_ =>
        {
            rig.Pool.AddExistingServer("n1", replacement);
            return ValueTask.CompletedTask;
        });
#pragma warning restore CA2012
        await rig.Pool.RemoveServerAsync("n1");
        rig.Pool.GetServer("n1").Should().BeSameAs(replacement, "release code can call the pool because no membership lock is held");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Follower_ShouldLeaveNoSubscription_WhenRemovalFallsInEitherWalkWindow(bool afterRecheck)
    {
        await using var rig = new Rig();
        var server = rig.NewServer();
        rig.Pool.AddExistingServer("n1", server);
        using var follower = new Follower(rig.Pool);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var removing = new ManualResetEventSlim();
        var candidate = rig.Pool.Servers.Single();
        var removalNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Pool.ServerRemoved += (_, _) => removalNotified.TrySetResult();
        Action gate = () => { entered.Set(); release.Wait(Bound).Should().BeTrue(); };
        if (afterRecheck) follower.AfterRecheck = gate;
        var walk = Task.Run(() =>
        {
            if (!afterRecheck) gate();
            follower.Walk([candidate]);
        });
        Task? removal = null;
        try
        {
            entered.Wait(Bound).Should().BeTrue();
            removal = Task.Run(async () => { removing.Set(); await rig.Pool.RemoveServerAsync("n1"); });
            removing.Wait(Bound).Should().BeTrue();
            if (afterRecheck)
            {
                SpinWait.SpinUntil(() => rig.Pool.GetServer("n1") is null, Bound).Should().BeTrue("removal mutates membership while the walker still holds its subscriber lock"); // fence-allow: GUARD-TIMEOUT — failure bound on an observed membership change, never the winning arm of a green run
                removal.IsCompleted.Should().BeFalse("the removal callback must wait for installation to finish");
            }
            else
            {
                await removal.WaitAsync(Bound);
                removalNotified.Task.IsCompletedSuccessfully.Should().BeTrue("the removal notification finished before the stale walk continues");
            }
        }
        finally
        {
            release.Set();
            await walk.WaitAsync(Bound);
            if (removal is not null) await removal.WaitAsync(Bound);
        }
        follower.OwnedCount.Should().Be(0);
        follower.Installs.Should().Be(afterRecheck ? 1 : 0);
        server.Agents.OnAgentLogin("removed");
        follower.LoginCount.Should().Be(0);
    }

    [Fact]
    public async Task Follower_ShouldInstallCurrentInstanceOnce_WhenWalkDuplicatesAddedAndOldRemoval()
    {
        await using var rig = new Rig();
        using var follower = new Follower(rig.Pool);
        var old = rig.NewServer();
        rig.Pool.AddExistingServer("n1", old);
        await rig.Pool.RemoveServerAsync("n1");
        var current = rig.NewServer();
        rig.Pool.AddExistingServer("n1", current);
        follower.Walk(rig.Pool.Servers);
        follower.Removed("n1", old);
        follower.Walk([new("n1", old)]);
        follower.OwnedCount.Should().Be(1);
        follower.Installs.Should().Be(2);
        old.Agents.OnAgentLogin("old");
        current.Agents.OnAgentLogin("new");
        follower.LoginCount.Should().Be(1);
    }

    [Fact]
    public async Task Follower_ShouldUndoPartialInstallation_WhenInstallationThrows()
    {
        await using var rig = new Rig();
        using var follower = new Follower(rig.Pool) { FailAfterSubscribe = true };
        var server = rig.NewServer();
        rig.Pool.AddExistingServer("n1", server);
        follower.OwnedCount.Should().Be(0);
        server.Agents.OnAgentLogin("partial");
        follower.LoginCount.Should().Be(0);
        follower.FailAfterSubscribe = false;
        follower.Walk(rig.Pool.Servers);
        follower.OwnedCount.Should().Be(1);
        server.Agents.OnAgentLogin("installed");
        follower.LoginCount.Should().Be(1);
        await rig.Pool.RemoveServerAsync("n1");
        follower.OwnedCount.Should().Be(0);
        server.Agents.OnAgentLogin("removed");
        follower.LoginCount.Should().Be(1);
    }

    // Observe the competing thread actually blocked in the synchronous membership entry, or observe its completion.
    // Merely seeing that it was scheduled would let an unlocked mutation win by scheduling luck.
    private static void AssertWaitingOnMembership(Task operation, Thread caller)
    {
        SpinWait.SpinUntil(() => operation.IsCompleted || (caller.ThreadState & ThreadState.WaitSleepJoin) != ThreadState.Running, Bound) // fence-allow: GUARD-TIMEOUT — failure bound on the competing caller blocking or completing, never the winning arm of a green run
            .Should().BeTrue("the competing membership caller reaches a blocked or completed state before the handler gate opens");
        operation.IsCompleted.Should().BeFalse("the gated notification must finish before the competing membership operation");
    }

    private sealed class Follower : IDisposable
    {
        private readonly Lock _sync = new();
        private readonly VerbaraServerPool _pool;
        private readonly Dictionary<VerbaraServer, Action<AsteriskAgent>> _owned = [];
        private int _logins;
        public int LoginCount => Volatile.Read(ref _logins);
        public int Installs { get; private set; }
        public int OwnedCount { get { lock (_sync) return _owned.Count; } }
        public Action? AfterRecheck { get; set; }
        public bool FailAfterSubscribe { get; set; }
        public Follower(VerbaraServerPool pool)
        {
            _pool = pool;
            lock (_sync)
            {
                pool.ServerAdded += InstallIfCurrent;
                pool.ServerRemoved += Removed;
            }
        }
        public void Walk(IEnumerable<KeyValuePair<string, VerbaraServer>> candidates)
        {
            foreach (var (id, server) in candidates) InstallIfCurrent(id, server);
        }
        private void InstallIfCurrent(string id, VerbaraServer server)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_pool.GetServer(id), server) || _owned.ContainsKey(server)) return;
                AfterRecheck?.Invoke();
                Action<AsteriskAgent> handler = _ => Interlocked.Increment(ref _logins);
                try
                {
                    server.Agents.AgentLoggedIn += handler;
                    if (FailAfterSubscribe) throw new InvalidOperationException("partial installation");
                    _owned.Add(server, handler);
                    Installs++;
                }
                catch
                {
                    server.Agents.AgentLoggedIn -= handler;
                    _owned.Remove(server);
                    throw;
                }
            }
        }
        public void Removed(string id, VerbaraServer server)
        {
            lock (_sync)
                if (_owned.Remove(server, out var handler)) server.Agents.AgentLoggedIn -= handler;
        }
        public void Dispose()
        {
            lock (_sync)
            {
                _pool.ServerAdded -= InstallIfCurrent;
                _pool.ServerRemoved -= Removed;
                foreach (var (server, handler) in _owned) server.Agents.AgentLoggedIn -= handler;
                _owned.Clear();
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TaskCompletionSource LoadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentBag<IAmiConnection> DisposedConnections = [];
        public VerbaraServerPool Pool { get; }
        public int ConnectionsCreated { get; private set; }
        public Rig(bool gated = false, bool fail = false, ILoggerFactory? logs = null)
        {
            var factory = Substitute.For<IAmiConnectionFactory>();
#pragma warning disable CA2012 // NSubstitute setup evaluates the ValueTask.
            factory.CreateAndConnectAsync(Arg.Any<AmiConnectionOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                ConnectionsCreated++;
                return new ValueTask<IAmiConnection>(Connection(gated, fail));
            });
#pragma warning restore CA2012
            Pool = new VerbaraServerPool(factory, logs ?? NullLoggerFactory.Instance);
        }
        public VerbaraServer NewServer() => new(Connection(false, false), NullLogger<VerbaraServer>.Instance);
        private IAmiConnection Connection(bool gated, bool fail)
        {
            var connection = Substitute.For<IAmiConnection>();
            connection.State.Returns(AmiConnectionState.Initial);
            connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
            connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
                .Returns(c => Load(gated, fail, c.ArgAt<CancellationToken>(1)));
#pragma warning disable CA2012 // NSubstitute setup evaluates the ValueTask.
            connection.DisposeAsync().Returns(_ => { DisposedConnections.Add(connection); return ValueTask.CompletedTask; });
#pragma warning restore CA2012
            return connection;
        }
        private async IAsyncEnumerable<ManagerEvent> Load(bool gated, bool fail, [EnumeratorCancellation] CancellationToken ct)
        {
            if (gated)
            {
                LoadEntered.TrySetResult();
                await ReleaseLoad.Task.WaitAsync(ct);
            }
            if (fail) throw new AmiNotConnectedException("start failed");
            yield break;
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseLoad.TrySetResult();
            await Pool.DisposeAsync();
        }
    }

    private sealed class Logs : ILoggerFactory
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class Recorder(Logs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => owner.Entries.Add((logLevel, exception));
        }
    }
}
