using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Enums;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.TestInfrastructure;
using Verbara.Sdk.TestInfrastructure.Containers;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

/// <summary>
/// The reconnect loop's give-up, announced on <see cref="IAmiConnection.StateChanged"/> against a real Asterisk that
/// no longer accepts the connection's AMI user.
/// </summary>
/// <remarks>
/// <para>
/// The user is removed from <c>manager.conf</c> and the configuration reloaded. A reload keeps the sessions already
/// logged in (measured on 22.9.0 and 23.4.1: the removed user's session still answers <c>Ping</c>, and a new login
/// with it is refused), so the session is then ended with <c>manager kick session</c>, which closes its socket while
/// Asterisk keeps running. Every reconnect attempt therefore reaches an Asterisk that refuses the login. A restart
/// would end the session too, but the attempts made while Asterisk is down fail to connect at all, so the last one's
/// error would depend on how long the restart took.
/// </para>
/// <para>
/// The container is this class's alone, because its configuration changes: it mounts a configuration written per run,
/// with an AMI user whose secret is generated for the run.
/// </para>
/// </remarks>
[Trait("Category", "Functional")]
public sealed class AmiGiveUpAnnouncementTests : FunctionalTestBase, IClassFixture<AmiGiveUpAnnouncementTests.OwnAsterisk>
{
    private const int MaxReconnectAttempts = 2;

    /// <summary>A hang bound for the give-up: two refused logins 200 ms apart.</summary>
    private static readonly TimeSpan GiveUpBound = TimeSpan.FromSeconds(60);

    private readonly OwnAsterisk _asterisk;
    private readonly ITestOutputHelper _output;

    public AmiGiveUpAnnouncementTests(OwnAsterisk asterisk, ITestOutputHelper output)
    {
        _asterisk = asterisk;
        _output = output;
    }

    [Fact]
    public async Task StateChanged_ShouldAnnounceOneFinalDisconnectedWithTheAuthenticationFailure_WhenEveryReconnectAttemptIsRefused()
    {
        _output.WriteLine(_asterisk.Version);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _asterisk.WriteManagerConfAsync(withUser: true, bound.Token);

        await using var connection = AmiConnectionFactory.Create(_asterisk.AmiHost, OwnAsterisk.AmiPort, LoggerFactory, opts =>
        {
            opts.Username = OwnAsterisk.User;
            opts.Password = _asterisk.Secret;
            opts.AutoReconnect = true;
            opts.MaxReconnectAttempts = MaxReconnectAttempts;
            opts.ReconnectInitialDelay = TimeSpan.FromMilliseconds(200);
            opts.ReconnectMultiplier = 1.0;
        });
        await connection.ConnectAsync(bound.Token);
        connection.State.Should().Be(AmiConnectionState.Connected);

        var log = new NotificationLog();
        connection.StateChanged += log.Add;
        connection.Reconnected += log.AddReconnected;

        await _asterisk.WriteManagerConfAsync(withUser: false, bound.Token);
        await _asterisk.KickUserSessionAsync(bound.Token);

        AmiConnectionStateChange final;
        try
        {
            final = await log.Final.WaitAsync(GiveUpBound);
        }
        catch (TimeoutException)
        {
            Assert.Fail(string.Create(CultureInfo.InvariantCulture, $"no final change was announced within {GiveUpBound}; delivered:\n{string.Join('\n', log.Snapshot())}"));
            throw;
        }

        var entries = log.Snapshot();
        foreach (var entry in entries)
            _output.WriteLine(entry.ToString());

        var changes = entries.Where(e => e.Change is not null).Select(e => e.Change!).ToList();
        using (new AssertionScope())
        {
            final.Current.Should().Be(AmiConnectionState.Disconnected);
            final.ByCaller.Should().BeFalse();
            final.Cause.Should().BeOfType<AmiAuthenticationException>("the give-up carries its last attempt's error, and Asterisk refused that login");
            changes.Count(c => c.IsFinal).Should().Be(1, "the connection gives up once");
            changes[^1].Should().BeSameAs(final, "nothing changes the state after the give-up");
            changes[0].IsLoss.Should().BeTrue("the kick ended the session without the caller asking");
            changes[0].Cause.Should().BeNull("Asterisk closed the session, which the transport reports as an end of stream");
            for (var i = 1; i < changes.Count; i++)
                changes[i].Previous.Should().Be(changes[i - 1].Current, "each change starts where the one before it ended (change {0})", i);

            var refused = changes.Where(c => c is { Previous: AmiConnectionState.Connecting, Current: AmiConnectionState.Reconnecting }).ToList();
            refused.Should().HaveCount(MaxReconnectAttempts, "MaxReconnectAttempts = {0} makes that many attempts before the give-up", MaxReconnectAttempts);
            refused.Should().OnlyContain(c => c.Cause is AmiAuthenticationException, "every attempt reached Asterisk, which refused the login");
            changes.Should().OnlyContain(c => !c.ByCaller, "the kick and the reconnect loop made every change");
            entries.Should().NotContain(e => e.Change == null, "no attempt logged in, so nothing reconnected");
        }

        connection.State.Should().Be(AmiConnectionState.Disconnected);
    }

    // ── The Asterisk of this class's own ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An Asterisk container, its network and its configuration, for this class alone. Both are named
    /// <c>{prefix}give-up-{run}</c>: the prefix is <c>verbara-</c>, or <c>VERBARA_FUNCTIONAL_DOCKER_PREFIX</c> when it is
    /// set, so a machine shared by several runs can tell its own resources apart.
    /// </summary>
    public sealed class OwnAsterisk : IAsyncLifetime
    {
        public const int AmiPort = 5038;
        public const string User = "giveup";

        private string? _configDirectory;

        /// <summary>The AMI user's secret, generated for this run and written only to its own configuration.</summary>
        public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public string AmiHost => (Container ?? throw NotStarted()).NetworkAddress;

        /// <summary>What <c>core show version</c> printed in the container.</summary>
        public string Version { get; private set; } = "";

        // Released by DisposeAsync, which xUnit calls through IAsyncLifetime.
        private INetwork? Network { get; set; }

        private AsteriskContainer? Container { get; set; }

        public async Task InitializeAsync()
        {
            var prefix = Environment.GetEnvironmentVariable("VERBARA_FUNCTIONAL_DOCKER_PREFIX") ?? "verbara-";
            var name = prefix + "give-up-" + Guid.NewGuid().ToString("N")[..8];
            _configDirectory = WriteConfiguration(Path.Join(Path.GetTempPath(), name));
            WriteManagerConf(_configDirectory, Secret, withUser: true);

            var image = await AsteriskContainer.CreateImageAsync();
            Network = new NetworkBuilder().WithName(name).Build();
            await Network.CreateAsync();
            Container = new AsteriskContainer(Network, image, _configDirectory, name, publishPorts: false);
            await Container.StartAsync();

            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            // The CLI command itself waits for the end of the start; it fails only while no Asterisk runs to take it.
            while ((await Container.ExecAsync(["asterisk", "-rx", "core waitfullybooted"], ready.Token)).ExitCode != 0)
                ready.Token.ThrowIfCancellationRequested();
            Version = (await Container.ExecAsync(["asterisk", "-rx", "core show version"], ready.Token)).Stdout.Trim();
        }

        /// <summary>Writes <c>manager.conf</c> with or without the test's AMI user, and has Asterisk reload it.</summary>
        public async Task WriteManagerConfAsync(bool withUser, CancellationToken cancellationToken)
        {
            WriteManagerConf(_configDirectory ?? throw NotStarted(), Secret, withUser);
            var reload = await RunCliAsync("manager reload", cancellationToken);
            reload.ExitCode.Should().Be(0, "manager reload runs: {0}", reload.Stderr);
        }

        /// <summary>Ends the test user's AMI session from the Asterisk side, leaving Asterisk running.</summary>
        public async Task KickUserSessionAsync(CancellationToken cancellationToken)
        {
            var connected = (await RunCliAsync("manager show connected", cancellationToken)).Stdout;
            // Columns: Username, IP Address, Start, Elapsed, FileDes, …
            var match = Regex.Match(connected, $@"^\s*{User}\s+\S+\s+\d+\s+\d+\s+(\d+)", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            match.Success.Should().BeTrue("the user's session is listed as connected:\n{0}", connected);

            var fileDescriptor = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var kick = await RunCliAsync(string.Create(CultureInfo.InvariantCulture, $"manager kick session {fileDescriptor}"), cancellationToken);
            kick.Stdout.Should().Contain("Kicking manager session", "Asterisk reports the kick: {0}", kick.Stderr);
        }

        public async Task DisposeAsync()
        {
            if (Container is not null)
                await Container.DisposeAsync();

            if (Network is not null)
                await Network.DisposeAsync();

            if (_configDirectory is not null && Directory.Exists(_configDirectory))
                Directory.Delete(_configDirectory, recursive: true);
        }

        private Task<DotNet.Testcontainers.Containers.ExecResult> RunCliAsync(string command, CancellationToken cancellationToken) =>
            (Container ?? throw NotStarted()).ExecAsync(["asterisk", "-rx", command], cancellationToken);

        private static InvalidOperationException NotStarted() => new("The container has not been started.");

        /// <summary>
        /// Writes this class's own configuration but <c>manager.conf</c>: the functional <c>asterisk.conf</c>, and no
        /// realtime and no database, so nothing Asterisk loads waits on a server this container does not have.
        /// </summary>
        private static string WriteConfiguration(string directory)
        {
            Directory.CreateDirectory(directory);
            File.Copy(Path.Join(DockerPaths.AsteriskConfig, "asterisk.conf"), Path.Join(directory, "asterisk.conf"));
            Write(directory, "logger.conf", """
                [general]

                [logfiles]
                console => notice,warning,error
                """);
            Write(directory, "modules.conf", """
                [modules]
                autoload = yes
                noload = chan_sip.so
                noload = chan_skinny.so
                noload = chan_mgcp.so
                noload = res_config_pgsql.so
                noload = res_config_odbc.so
                noload = res_odbc.so
                noload = res_config_ldap.so
                noload = res_config_sqlite3.so
                """);
            Write(directory, "extensions.conf", """
                [general]
                """);
            return directory;
        }

        private static void WriteManagerConf(string directory, string secret, bool withUser)
        {
            var user = withUser
                ? $"""

                  [{User}]
                  secret = {secret}
                  read = system,call,agent,user,config,originate,reporting,command,dtmf,cdr
                  write = system,call,agent,user,config,originate,command,reporting,dtmf
                  deny = 0.0.0.0/0.0.0.0
                  permit = 0.0.0.0/0.0.0.0
                  """
                : "";
            Write(directory, "manager.conf", $"""
                [general]
                enabled = yes
                bindaddr = 0.0.0.0
                port = {AmiPort}
                {user}
                """);
        }

        private static void Write(string directory, string file, string text) =>
            File.WriteAllText(Path.Join(directory, file), text + "\n");
    }
}
