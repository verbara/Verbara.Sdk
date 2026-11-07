using System.Globalization;
using Verbara.Sdk;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Agi.Server;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Outbound;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Extension methods for registering Verbara Sdk services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add all Verbara Sdk services (AMI, AGI, ARI, Live) to the service collection.
    /// Configures a single Asterisk server connection with options validation on startup.
    /// </summary>
    /// <remarks>
    /// Registers the health checks <c>ami</c>, <c>live</c>, <c>agi</c> and, when <see cref="VerbaraOptions.Ari"/> is set,
    /// <c>ari</c>, with no tags unless the host names them in <see cref="VerbaraHealthCheckOptions.Tags"/> (set before or
    /// after this call). Never name the tag your readiness probe reads: an Asterisk outage would take the pod out of
    /// service.
    /// </remarks>
    public static IServiceCollection AddVerbara(
        this IServiceCollection services,
        Action<VerbaraOptions> configure)
    {
        var options = new VerbaraOptions();
        configure(options);

        // Transport
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();

        // AMI (single-server) with AOT-safe source-generated validation
        // Every option is copied, so a value set here replaces one a Configure<T> placed before AddVerbara set,
        // and a Configure<T> placed after it adjusts what AddVerbara set.
        services.AddOptions<AmiConnectionOptions>()
            .Configure(o => CopyAmiOptions(options.Ami, o))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AmiConnectionOptions>, AmiConnectionOptionsValidator>();
        services.TryAddSingleton<IAmiConnection, AmiConnection>();

        // AMI Factory (always available for creating additional connections)
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();

        // AGI
        var mappingStrategy = options.AgiMappingStrategy ?? new SimpleMappingStrategy();
        services.TryAddSingleton<IMappingStrategy>(mappingStrategy);
        services.TryAddSingleton<IAgiServer>(sp =>
            new FastAgiServer(
                options.AgiPort,
                sp.GetRequiredService<IMappingStrategy>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FastAgiServer>>()));
        AddSdkHealthCheck(services, "agi",
            sp => ActivatorUtilities.GetServiceOrCreateInstance<Verbara.Sdk.Agi.Diagnostics.AgiHealthCheck>(sp), guarded: false);
        services.AddSingleton<IHostedService, Verbara.Sdk.Agi.Hosting.AgiHostedService>();

        // Live
        services.TryAddSingleton<VerbaraServer>();
        services.TryAddSingleton<IVerbaraServer>(sp => sp.GetRequiredService<VerbaraServer>());
        AddSdkHealthCheck(services, "live",
            sp => ActivatorUtilities.GetServiceOrCreateInstance<Verbara.Sdk.Live.Diagnostics.LiveHealthCheck>(sp), guarded: false);

        // Hosted services for automatic lifecycle management
        services.AddSingleton<IHostedService, AmiConnectionHostedService>();
        services.AddSingleton<IHostedService, VerbaraServerHostedService>();

        // Health checks
        AddSdkHealthCheck(services, "ami",
            sp => ActivatorUtilities.GetServiceOrCreateInstance<Verbara.Sdk.Ami.Diagnostics.AmiHealthCheck>(sp), guarded: false);

        // ARI with validation
        if (options.Ari is not null)
        {
            AddAriClient(services, options.Ari);
        }

        return services;
    }

    /// <summary>
    /// Add the ARI client and nothing else: no AMI connection, no AGI server, no Live server and none of their health
    /// checks. For a host that needs only ARI; with <see cref="AriClientOptions.EventsEnabled"/> set to
    /// <see langword="false"/> such a host starts and stops with no Asterisk reachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers what <see cref="AddVerbara(IServiceCollection, Action{VerbaraOptions})"/> registers for ARI: the
    /// options, with every value set in <paramref name="configure"/> delivered and validated on start, the client, the
    /// <c>ari</c> health check, the hosted service that connects the client and, when
    /// <see cref="AriClientOptions.ConfigureAudioServer"/> is set, the audio servers and their hosted service.
    /// <paramref name="configure"/> runs once, here, on a new <see cref="AriClientOptions"/>.
    /// </para>
    /// <para>
    /// The ARI client is registered once: calling this method twice, or on a collection where <c>AddVerbara</c> set its
    /// <see cref="VerbaraOptions.Ari"/>, throws <see cref="InvalidOperationException"/>. The session engine
    /// (<c>AddVerbaraSessions</c>) reads the Live server, which only <c>AddVerbara</c> registers.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddVerbaraAri(
        this IServiceCollection services,
        Action<AriClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AriClientOptions();
        configure(options);
        AddAriClient(services, options);
        return services;
    }

    // The ARI registration of AddVerbara and AddVerbaraAri: options with every value copied and validated on start, the
    // validator, the audio servers when ConfigureAudioServer is set, the client, the ari check and its hosted service.
    // Registered once: a second registration would add a second ari check, which fails when the health check service is
    // resolved, and a client built from the first options while the second options' Configure also runs.
    private static void AddAriClient(IServiceCollection services, AriClientOptions ariOptions)
    {
        if (services.Any(d => d.ServiceType == typeof(AriClientRegistration)))
            throw new InvalidOperationException(
                "The ARI client is already registered. Register it once: through AddVerbara(o => o.Ari = …) or through " +
                "AddVerbaraAri(…), not both and not twice.");
        services.AddSingleton(AriClientRegistration.Instance);

        services.AddOptions<AriClientOptions>()
            .Configure(o => CopyAriOptions(ariOptions, o))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AriClientOptions>, AriClientOptionsValidator>();

        // Audio servers (AudioSocket + WebSocket)
        if (ariOptions.ConfigureAudioServer is not null)
        {
            var audioOpts = new AudioServerOptions();
            ariOptions.ConfigureAudioServer(audioOpts);
            services.AddSingleton(audioOpts);
            services.TryAddSingleton<AudioSocketServer>();

            if (audioOpts.WebSocketPort > 0)
                services.TryAddSingleton<WebSocketAudioServer>();

            services.TryAddSingleton<IAudioServer>(sp =>
            {
                var servers = new List<IAudioServer> { sp.GetRequiredService<AudioSocketServer>() };
                var ws = sp.GetService<WebSocketAudioServer>();
                if (ws is not null) servers.Add(ws);
                return new CompositeAudioServer(servers);
            });

            services.AddSingleton<IHostedService, AriAudioHostedService>();
        }

        // Use factory to safely inject optional IAudioServer
        services.TryAddSingleton<IAriClient>(sp => new AriClient(
            sp.GetRequiredService<IOptions<AriClientOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AriClient>>(),
            sp.GetService<IAudioServer>()));

        AddSdkHealthCheck(services, "ari",
            sp => ActivatorUtilities.GetServiceOrCreateInstance<Verbara.Sdk.Ari.Diagnostics.AriHealthCheck>(sp), guarded: false);
        services.AddSingleton<IHostedService, AriConnectionHostedService>();
    }

    /// <summary>
    /// Registers the host's health-check options once (validated on start) and adds one SDK health check that carries
    /// the host's tags (<see cref="VerbaraHealthCheckOptions.Tags"/>). Guarded: adds nothing when a registration of that
    /// name already exists — another SDK call's or the host's own — and tags nothing it did not create.
    /// </summary>
    private static void AddSdkHealthCheck(
        IServiceCollection services,
        string name,
        Func<IServiceProvider, IHealthCheck> factory,
        bool guarded)
    {
        services.AddHealthChecks();
        services.AddOptions<VerbaraHealthCheckOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<VerbaraHealthCheckOptions>, VerbaraHealthCheckOptionsValidator>());
        services.AddOptions<HealthCheckServiceOptions>().Configure<IOptions<VerbaraHealthCheckOptions>>((health, host) =>
        {
            // Read first: a blank tag fails here even when the guard below returns.
            var tags = host.Value.Tags;
            if (guarded)
            {
                foreach (var registration in health.Registrations)
                {
                    if (string.Equals(registration.Name, name, StringComparison.Ordinal))
                        return;
                }
            }

            health.Registrations.Add(new HealthCheckRegistration(
                name,
                factory,
                failureStatus: null,
                tags: tags.Count == 0 ? null : tags));
        });
    }

    /// <summary>
    /// Add all Verbara Sdk services binding options from <see cref="IConfiguration"/>.
    /// Binds every option of <see cref="AmiConnectionOptions"/> from the <c>Asterisk:Ami</c> section and of
    /// <see cref="AriClientOptions"/> from the <c>Asterisk:Ari</c> section (the ARI client is registered only when that
    /// section exists), and the AGI port from <c>Asterisk:AgiPort</c>. Values are parsed with the invariant culture, and
    /// a value that cannot be converted throws an <see cref="InvalidOperationException"/> naming its key.
    /// AOT-safe: the binding is emitted by the configuration-binding source generator, not done by reflection.
    /// </summary>
    /// <remarks>
    /// Also reads <c>Asterisk:HealthCheckTags</c> (an array) into <see cref="VerbaraHealthCheckOptions.Tags"/>, added to
    /// whatever a <c>Configure</c> adds; the health checks carry no tags unless the host names them there. A blank value
    /// fails the options' validation. Never name the tag your readiness probe reads: an Asterisk outage would take the
    /// pod out of service.
    /// </remarks>
    public static IServiceCollection AddVerbara(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var ami = configuration.GetSection("Asterisk:Ami");
        var ari = configuration.GetSection("Asterisk:Ari");
        var healthCheckTags = configuration.GetSection("Asterisk:HealthCheckTags").Get<string[]>();

        services.AddVerbara(o =>
        {
            ami.Bind(o.Ami);

            if (int.TryParse(configuration["Asterisk:AgiPort"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var agiPort))
                o.AgiPort = agiPort;

            if (ari.Exists())
            {
                o.Ari = new AriClientOptions();
                ari.Bind(o.Ari);
            }
        });

        // Added, never filtered: a blank value fails the options' validation like any blank tag.
        if (healthCheckTags is { Length: > 0 })
        {
            services.Configure<VerbaraHealthCheckOptions>(o =>
            {
                foreach (var tag in healthCheckTags)
                    o.Tags.Add(tag);
            });
        }

        return services;
    }

    /// <summary>Copies every settable option of <paramref name="source"/> onto <paramref name="target"/>.</summary>
    private static void CopyAmiOptions(AmiConnectionOptions source, AmiConnectionOptions target)
    {
        target.Hostname = source.Hostname;
        target.Port = source.Port;
        target.Username = source.Username;
        target.Password = source.Password;
        target.UseSsl = source.UseSsl;
        target.ConnectionTimeout = source.ConnectionTimeout;
        target.ReadTimeout = source.ReadTimeout;
        target.DefaultResponseTimeout = source.DefaultResponseTimeout;
        target.DefaultEventTimeout = source.DefaultEventTimeout;
        target.AutoReconnect = source.AutoReconnect;
        target.MaxReconnectAttempts = source.MaxReconnectAttempts;
        target.EventPumpCapacity = source.EventPumpCapacity;
        target.ReconnectInitialDelay = source.ReconnectInitialDelay;
        target.ReconnectMaxDelay = source.ReconnectMaxDelay;
        target.ReconnectMultiplier = source.ReconnectMultiplier;
        target.EnableHeartbeat = source.EnableHeartbeat;
        target.HeartbeatInterval = source.HeartbeatInterval;
        target.HeartbeatTimeout = source.HeartbeatTimeout;
    }

    /// <summary>Copies every settable option of <paramref name="source"/> onto <paramref name="target"/>, the audio-server callback included.</summary>
    private static void CopyAriOptions(AriClientOptions source, AriClientOptions target)
    {
        target.ConfigureAudioServer = source.ConfigureAudioServer;
        target.BaseUrl = source.BaseUrl;
        target.Username = source.Username;
        target.Password = source.Password;
        target.Application = source.Application;
        target.EventsEnabled = source.EventsEnabled;
        target.AutoReconnect = source.AutoReconnect;
        target.ReconnectInitialDelay = source.ReconnectInitialDelay;
        target.ReconnectMaxDelay = source.ReconnectMaxDelay;
        target.ReconnectMultiplier = source.ReconnectMultiplier;
        target.MaxReconnectAttempts = source.MaxReconnectAttempts;
    }

    /// <summary>
    /// Add session engine services (CallSessionManager, extension points, hosted service).
    /// Auto-attaches to the single <see cref="VerbaraServer"/> on startup, before the server's first load, so calls
    /// already in progress at start have a session.
    /// Call after <see cref="AddVerbara(IServiceCollection, Action{VerbaraOptions})"/> for single-server deployments.
    /// </summary>
    public static IServiceCollection AddVerbaraSessions(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        services.AddSingleton<IHostedService, SessionManagerHostedService>();
        services.AddSingleton<IHostedService, SessionReconciliationService>();
        services.TryAddSingleton(SingleServerSweepRegistration.Instance);
        return services;
    }

    /// <summary>
    /// Add session engine services and return an <see cref="ISessionsBuilder"/> so backend
    /// packages (Redis, Postgres, ...) can register themselves fluently:
    /// <c>services.AddVerbaraSessionsBuilder().UseRedis(...)</c>.
    /// Behaves identically to <see cref="AddVerbaraSessions(IServiceCollection, Action{SessionOptions})"/>: registers the InMemory
    /// default store via <c>TryAddSingleton</c> and wires the auto-attach hosted services, which attach
    /// before the server's first load, so calls already in progress at start have a session.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsBuilder(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        services.AddSingleton<IHostedService, SessionManagerHostedService>();
        services.AddSingleton<IHostedService, SessionReconciliationService>();
        services.TryAddSingleton(SingleServerSweepRegistration.Instance);
        return new SessionsBuilder(services);
    }

    /// <summary>
    /// Add session engine services for multi-server deployments using <see cref="VerbaraServerPool"/>, and the
    /// reconciliation sweep of the pool. Call after <see cref="AddVerbaraMultiServer"/> for clustered deployments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Servers are not attached for you: attach each one with <see cref="CallSessionManager.AttachToServer"/> under
    /// the id it has in the pool, and detach it with <see cref="CallSessionManager.DetachFromServer(string)"/>, or with
    /// <see cref="CallSessionManager.DetachFromServer(string, DetachedCalls)"/> to choose what happens to its calls.
    /// The one-argument detach keeps them held, which is right when another instance takes the server over; for a
    /// server removed for good, <see cref="DetachedCalls.Release"/> lets go of its calls in progress without ending
    /// them and reports each with a <see cref="CallReleasedEvent"/>, so the sweep no longer counts them as
    /// <c>serverless</c>.
    /// </para>
    /// <para>
    /// The sweep is a hosted service. On each <see cref="SessionOptions.ReconciliationInterval"/> tick (default 30
    /// seconds) it walks the servers the pool holds and, for each one, verifies the held calls attached under that
    /// server's id that are older than <see cref="SessionOptions.DialingTimeout"/> against one <c>Status</c> of that
    /// server, sent only when there is such a call. A call whose channels the completed snapshot omits ends as a reload
    /// ends it (<c>cause=reload</c>, no hangup cause); a call Asterisk still lists is left alone. A server whose
    /// verification fails is logged and skipped for that tick; the other servers are verified. A held call whose
    /// server is not in the pool is left alone. <see cref="Timeout.InfiniteTimeSpan"/> switches the sweep off
    /// (<c>configure: o =&gt; o.ReconciliationInterval = Timeout.InfiniteTimeSpan</c>); any other interval of zero or
    /// less fails the host's start with <see cref="ArgumentOutOfRangeException"/>. Calling this method more than once
    /// registers one sweep; on a host that also calls <see cref="AddVerbaraSessions(IServiceCollection, Action{SessionOptions})"/>, the single DI server is left to
    /// that registration's sweep.
    /// </para>
    /// <para>
    /// The sessions are found by the channel ids Asterisk issues, so the servers of a pool must not issue the same ids:
    /// give every Asterisk of the pool its own <c>systemname</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddVerbaraSessionsMultiServer(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        AddPoolSweep(services);
        return services;
    }

    /// <summary>
    /// Add session engine services for multi-server deployments, and the reconciliation sweep of the pool, and return an
    /// <see cref="ISessionsBuilder"/> so backend packages can register themselves fluently. Behaves identically to
    /// <see cref="AddVerbaraSessionsMultiServer(IServiceCollection, Action{SessionOptions})"/> otherwise, sweep included.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsMultiServerBuilder(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        AddPoolSweep(services);
        return new SessionsBuilder(services);
    }

    /// <summary>
    /// Add session engine services, binding every option from the <c>Sessions</c> section using the
    /// configuration-binding source generator. Configured context lists replace their defaults; an absent
    /// section keeps all defaults. Values use invariant culture; malformed values throw an
    /// <see cref="InvalidOperationException"/> naming their key. The optional delegate runs after binding.
    /// Registration and startup validation match <see cref="AddVerbaraSessions(IServiceCollection, Action{SessionOptions})"/>.
    /// </summary>
    public static IServiceCollection AddVerbaraSessions(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SessionOptions>? configure = null) =>
        services.AddVerbaraSessions(BindSessionOptions(configuration, configure));

    /// <summary>
    /// Add session engine services, binding every option from the <c>Sessions</c> section using the
    /// configuration-binding source generator. Configured context lists replace their defaults; an absent
    /// section keeps all defaults. Values use invariant culture; malformed values throw an
    /// <see cref="InvalidOperationException"/> naming their key. The optional delegate runs after binding.
    /// Registration and startup validation match <see cref="AddVerbaraSessionsBuilder(IServiceCollection, Action{SessionOptions})"/>.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsBuilder(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SessionOptions>? configure = null) =>
        services.AddVerbaraSessionsBuilder(BindSessionOptions(configuration, configure));

    /// <summary>
    /// Add session engine services, binding every option from the <c>Sessions</c> section using the
    /// configuration-binding source generator. Configured context lists replace their defaults; an absent
    /// section keeps all defaults. Values use invariant culture; malformed values throw an
    /// <see cref="InvalidOperationException"/> naming their key. The optional delegate runs after binding.
    /// Registration and startup validation match <see cref="AddVerbaraSessionsMultiServer(IServiceCollection, Action{SessionOptions})"/>.
    /// </summary>
    public static IServiceCollection AddVerbaraSessionsMultiServer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SessionOptions>? configure = null) =>
        services.AddVerbaraSessionsMultiServer(BindSessionOptions(configuration, configure));

    /// <summary>
    /// Add session engine services, binding every option from the <c>Sessions</c> section using the
    /// configuration-binding source generator. Configured context lists replace their defaults; an absent
    /// section keeps all defaults. Values use invariant culture; malformed values throw an
    /// <see cref="InvalidOperationException"/> naming their key. The optional delegate runs after binding.
    /// Registration and startup validation match <see cref="AddVerbaraSessionsMultiServerBuilder(IServiceCollection, Action{SessionOptions})"/>.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsMultiServerBuilder(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SessionOptions>? configure = null) =>
        services.AddVerbaraSessionsMultiServerBuilder(BindSessionOptions(configuration, configure));

    /// <summary>Builds the configure callback shared by the four configuration registrations.</summary>
    private static Action<SessionOptions> BindSessionOptions(
        IConfiguration configuration,
        Action<SessionOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Sessions");
        return options =>
        {
            // The generated binder appends to existing arrays. A child key also represents an explicitly empty
            // list, whose section can have neither a value nor children, so Exists() is insufficient here.
            foreach (var child in section.GetChildren())
            {
                if (string.Equals(child.Key, nameof(SessionOptions.InboundContextPatterns), StringComparison.OrdinalIgnoreCase))
                    options.InboundContextPatterns = [];
                else if (string.Equals(child.Key, nameof(SessionOptions.OutboundContextPatterns), StringComparison.OrdinalIgnoreCase))
                    options.OutboundContextPatterns = [];
            }

            section.Bind(options);
            configure?.Invoke(options);
        };
    }

    /// <summary>
    /// Registers the pool's reconciliation sweep once, however many multi-server registrations call it, through an
    /// explicit factory (no constructor reflection).
    /// </summary>
    private static void AddPoolSweep(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PoolReconciliationService>(sp =>
            new PoolReconciliationService(
                sp.GetRequiredService<ICallSessionManager>(),
                sp.GetService<VerbaraServerPool>(),
                sp.GetService<SingleServerSweepRegistration>() is null ? null : sp.GetService<VerbaraServer>(),
                sp.GetRequiredService<IOptions<SessionOptions>>(),
                sp.GetRequiredService<ILogger<PoolReconciliationService>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System)));

    /// <summary>
    /// Registered by the single-server sessions registrations: tells the pool sweep that the single DI server is
    /// verified by the single-server sweep.
    /// </summary>
    internal sealed class SingleServerSweepRegistration
    {
        public static readonly SingleServerSweepRegistration Instance = new();

        private SingleServerSweepRegistration()
        {
        }
    }

    private static IServiceCollection AddSessionsCore(
        IServiceCollection services,
        Action<SessionOptions>? configure)
    {
        services.TryAddSingleton<ICallSessionManager, CallSessionManager>();
        services.TryAddSingleton<IAgentSessionTracker, AgentSessionTracker>();
        services.TryAddSingleton<IQueueSessionTracker, QueueSessionTracker>();
        services.TryAddSingleton<SessionStoreBase, InMemorySessionStore>();
        // Resolve ISessionStore through SessionStoreBase so custom overrides registered
        // by consumers (e.g. AddSingleton<SessionStoreBase, MyStore>()) continue to flow
        // through to anyone depending on the interface. TryAdd keeps this idempotent.
        services.TryAddSingleton<ISessionStore>(sp => sp.GetRequiredService<SessionStoreBase>());

        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>();
        services.AddOptions<SessionOptions>().ValidateOnStart();
        // Guarded, not a plain AddCheck: a host may call more than one sessions registration (the single-server and the
        // multi-server one, or one of them twice), and a second registration under the same name makes
        // HealthCheckService throw "Duplicate health checks were registered" on resolve.
        AddSdkHealthCheck(
            services,
            SessionsHealthCheckName,
            sp => new Verbara.Sdk.Sessions.Diagnostics.SessionHealthCheck(sp.GetRequiredService<ICallSessionManager>()),
            guarded: true);
        return services;
    }

    /// <summary>
    /// Register Verbara Sdk with multi-server support.
    /// Use <see cref="VerbaraServerPool"/> to add and manage multiple Asterisk server connections.
    /// </summary>
    /// <remarks>
    /// Also registers one <see cref="Verbara.Sdk.Live.Diagnostics.VerbaraServerPoolHealthCheck"/> under the name
    /// <c>verbara-pool</c>, with no tags: it reads the AMI connection of every server the pool holds when it runs.
    /// Calling this method more than once registers the check once.
    /// </remarks>
    public static IServiceCollection AddVerbaraMultiServer(
        this IServiceCollection services)
    {
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
        services.TryAddSingleton<IAriClientFactory, Verbara.Sdk.Ari.Client.AriClientFactory>();
        services.TryAddSingleton<VerbaraServerPool>();

        // Guarded, not a plain AddCheck: every call to this method adds this Configure, and a second registration
        // under the same name makes HealthCheckService throw "Duplicate health checks were registered" on resolve.
        services.AddHealthChecks();
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations)
            {
                if (string.Equals(registration.Name, ServerPoolHealthCheckName, StringComparison.Ordinal))
                    return;
            }

            options.Registrations.Add(new HealthCheckRegistration(
                ServerPoolHealthCheckName,
                sp => new Verbara.Sdk.Live.Diagnostics.VerbaraServerPoolHealthCheck(sp.GetRequiredService<VerbaraServerPool>()),
                failureStatus: null,
                tags: null));
        });
        return services;
    }

    /// <summary>The name every sessions registration registers the session engine's health check under, once.</summary>
    private const string SessionsHealthCheckName = "sessions";

    /// <summary>The name <see cref="AddVerbaraMultiServer"/> registers the pool health check under.</summary>
    private const string ServerPoolHealthCheckName = "verbara-pool";

    /// <summary>
    /// Register an ARI Outbound WebSocket listener. Asterisk 22.5+ with
    /// <c>application=outbound</c> in <c>ari.conf</c> will dial this listener
    /// instead of the consumer dialing Asterisk. A singleton
    /// <see cref="IAriOutboundListener"/> is registered along with a hosted
    /// service that starts/stops it with the application lifecycle.
    /// </summary>
    public static IServiceCollection AddAriOutboundListener(
        this IServiceCollection services,
        Action<AriOutboundListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AriOutboundListenerOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AriOutboundListenerOptions>, AriOutboundListenerOptionsValidator>();

        services.TryAddSingleton<IAriOutboundListener, AriOutboundListener>();
        services.AddSingleton<IHostedService, AriOutboundListenerHostedService>();
        return services;
    }

    /// <summary>Marks a collection whose ARI client is registered, so a second registration fails where it is made.</summary>
    private sealed class AriClientRegistration
    {
        public static readonly AriClientRegistration Instance = new();
    }
}

/// <summary>
/// Top-level configuration for all Verbara Sdk services.
/// </summary>
public sealed class VerbaraOptions
{
    public AmiConnectionOptions Ami { get; set; } = new();
    public AriClientOptions? Ari { get; set; }
    public int AgiPort { get; set; } = 4573;
    public IMappingStrategy? AgiMappingStrategy { get; set; }
}
