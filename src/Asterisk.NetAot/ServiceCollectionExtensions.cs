using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Ami.Connection;
using Asterisk.NetAot.Ari.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.NetAot;

/// <summary>
/// Extension methods for registering Asterisk.NetAot services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add all Asterisk.NetAot services (AMI, AGI, ARI, Live, PBX) to the service collection.
    /// </summary>
    public static IServiceCollection AddAsteriskNetAot(
        this IServiceCollection services,
        Action<AsteriskNetAotOptions> configure)
    {
        var options = new AsteriskNetAotOptions();
        configure(options);

        // AMI
        services.Configure<AmiConnectionOptions>(o =>
        {
            o.Hostname = options.Ami.Hostname;
            o.Port = options.Ami.Port;
            o.Username = options.Ami.Username;
            o.Password = options.Ami.Password;
            o.UseSsl = options.Ami.UseSsl;
            o.AutoReconnect = options.Ami.AutoReconnect;
        });
        services.AddSingleton<IAmiConnection, AmiConnection>();

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
        }

        // TODO: Register AGI, Live, PBX services

        return services;
    }
}

/// <summary>
/// Top-level configuration for all Asterisk.NetAot services.
/// </summary>
public sealed class AsteriskNetAotOptions
{
    public AmiConnectionOptions Ami { get; set; } = new();
    public AriClientOptions? Ari { get; set; }
    public int AgiPort { get; set; } = 4573;
}
