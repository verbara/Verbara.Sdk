using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Live.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Extension methods for registering Asterisk SDK services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add all Asterisk SDK services (AMI, AGI, ARI, Live) to the service collection.
    /// Configures a single Asterisk server connection.
    /// </summary>
    public static IServiceCollection AddAsterisk(
        this IServiceCollection services,
        Action<AsteriskOptions> configure)
    {
        var options = new AsteriskOptions();
        configure(options);

        // Transport
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();

        // AMI (single-server)
        services.Configure<AmiConnectionOptions>(o =>
        {
            o.Hostname = options.Ami.Hostname;
            o.Port = options.Ami.Port;
            o.Username = options.Ami.Username;
            o.Password = options.Ami.Password;
            o.UseSsl = options.Ami.UseSsl;
            o.AutoReconnect = options.Ami.AutoReconnect;
        });
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

        // Live
        services.TryAddSingleton<AsteriskServer>();
        services.TryAddSingleton<IAsteriskServer>(sp => sp.GetRequiredService<AsteriskServer>());

        // ARI
        if (options.Ari is not null)
        {
            services.Configure<AriClientOptions>(o =>
            {
                o.BaseUrl = options.Ari.BaseUrl;
                o.Username = options.Ari.Username;
                o.Password = options.Ari.Password;
                o.Application = options.Ari.Application;
            });
            services.TryAddSingleton<IAriClient, AriClient>();
        }

        return services;
    }

    /// <summary>
    /// Register Asterisk SDK with multi-server support.
    /// Use <see cref="AsteriskServerPool"/> to add and manage multiple Asterisk server connections.
    /// </summary>
    public static IServiceCollection AddAsteriskMultiServer(
        this IServiceCollection services)
    {
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
        services.TryAddSingleton<AsteriskServerPool>();
        return services;
    }
}

/// <summary>
/// Top-level configuration for all Asterisk SDK services.
/// </summary>
public sealed class AsteriskOptions
{
    public AmiConnectionOptions Ami { get; set; } = new();
    public AriClientOptions? Ari { get; set; }
    public int AgiPort { get; set; } = 4573;
    public IMappingStrategy? AgiMappingStrategy { get; set; }
}
