using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.TestInfrastructure.Stacks;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>A runtime pool follower against Lab A's two real Asterisk servers.</summary>
[Trait("Category", "Functional")]
public sealed class PoolMembershipRuntimeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string AgentId = "pool-runtime";

    [Fact]
    public async Task ServerAdded_ShouldReachTheFollower_WhenANewServersFirstAgentLogsInAfterTheInitialWalk()
    {
        var lab = new MultiServerFixture();
        try
        {
            lab.WriteConfiguration();
            var directory = Path.Join(lab.ConfigDirectory, "b");
            await File.WriteAllTextAsync(Path.Join(directory, "agents.conf"), "[general]\n\n[" + AgentId + "]\nfullname = Runtime Agent\n");
            await File.AppendAllTextAsync(Path.Join(directory, "extensions.conf"),
                "\n[pool-membership]\nexten => login,1,Answer()\n same => n,AgentLogin(" + AgentId + ")\n same => n,Hangup()\n");
            await lab.InitializeAsync();
            await using var services = new ServiceCollection().AddLogging().AddVerbaraMultiServer().BuildServiceProvider();
            var pool = services.GetRequiredService<VerbaraServerPool>();
            await pool.AddServerAsync("a", Options(lab, false));
            using var follower = new Follower(pool);
            follower.Walk();
            follower.OwnedCount.Should().Be(1, "the follower finished its initial walk before B joins");
            var b = await pool.AddServerAsync("b", Options(lab, true));
            follower.OwnedCount.Should().Be(2, "the runtime Added callback installed B synchronously");
            var response = await b.Connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/login@pool-membership/n",
                Application = "Wait",
                Data = "60",
                IsAsync = true,
            });
            response.Response.Should().Be("Success");
            try
            {
                var seen = await follower.LoggedIn.Task.WaitAsync(Bound);
                seen.Server.Should().BeSameAs(b);
                seen.Agent.AgentId.Should().Be(AgentId);
                output.WriteLine("Lab A runtime add: B's first AgentLogin reached the exact-instance pool follower. "
                    + lab.ServerAVersion + "; " + lab.ServerBVersion);
            }
            finally
            {
                if (b.Agents.GetById(AgentId)?.Channel is { } channel)
                    await b.Connection.SendActionAsync(new HangupAction { Channel = channel });
            }
            await pool.RemoveServerAsync("b");
            follower.OwnedCount.Should().Be(1, "the removal uninstalled only B before its disposal");
        }
        finally { await lab.DisposeAsync(); }
    }

    private static AmiConnectionOptions Options(MultiServerFixture lab, bool serverB) => new()
    {
        Hostname = serverB ? lab.ServerBAddress : lab.ServerAAddress,
        Port = MultiServerAmiUsers.AmiPort,
        Username = MultiServerAmiUsers.Full.Name,
        Password = serverB ? lab.SecretB(MultiServerAmiUsers.Full) : lab.SecretA(MultiServerAmiUsers.Full),
        AutoReconnect = false,
    };

    private sealed class Follower : IDisposable
    {
        private readonly Lock _sync = new();
        private readonly VerbaraServerPool _pool;
        private readonly Dictionary<VerbaraServer, Action<AsteriskAgent>> _owned = [];
        public TaskCompletionSource<(VerbaraServer Server, AsteriskAgent Agent)> LoggedIn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OwnedCount { get { lock (_sync) return _owned.Count; } }
        public Follower(VerbaraServerPool pool)
        {
            _pool = pool;
            lock (_sync)
            {
                pool.ServerAdded += Install;
                pool.ServerRemoved += Remove;
            }
        }
        public void Walk()
        {
            foreach (var (id, server) in _pool.Servers) Install(id, server);
        }
        private void Install(string id, VerbaraServer server)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_pool.GetServer(id), server) || _owned.ContainsKey(server)) return;
                Action<AsteriskAgent> handler = agent => LoggedIn.TrySetResult((server, agent));
                try
                {
                    server.Agents.AgentLoggedIn += handler;
                    _owned.Add(server, handler);
                }
                catch
                {
                    server.Agents.AgentLoggedIn -= handler;
                    _owned.Remove(server);
                    throw;
                }
            }
        }
        private void Remove(string id, VerbaraServer server)
        {
            lock (_sync)
                if (_owned.Remove(server, out var handler)) server.Agents.AgentLoggedIn -= handler;
        }
        public void Dispose()
        {
            lock (_sync)
            {
                _pool.ServerAdded -= Install;
                _pool.ServerRemoved -= Remove;
                foreach (var (server, handler) in _owned) server.Agents.AgentLoggedIn -= handler;
                _owned.Clear();
            }
        }
    }
}
