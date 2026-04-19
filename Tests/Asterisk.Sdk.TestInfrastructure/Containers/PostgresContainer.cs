using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>Wraps a PostgreSQL 17 container for Asterisk realtime configuration.</summary>
public sealed class PostgresContainer : IAsyncDisposable
{
    private const string DbUser = "asterisk";
    private const string DbPassword = "asterisk";
    private const string DbName = "asterisk";

    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(5432);
    public string ContainerName => _container.Name;

    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={DbName};Username={DbUser};Password={DbPassword}";

    public PostgresContainer(INetwork? network = null)
    {
        var builder = new ContainerBuilder("postgres:18-alpine")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", DbUser)
            .WithEnvironment("POSTGRES_PASSWORD", DbPassword)
            .WithEnvironment("POSTGRES_DB", DbName)
            .WithBindMount(DockerPaths.FunctionalSqlDir, "/docker-entrypoint-initdb.d", AccessMode.ReadOnly)
            // UntilPortIsAvailable(5432) polls /proc/net/tcp which is never populated in
            // GitHub Actions CI, causing a 30-minute hang. pg_isready is bundled in
            // postgres:18-alpine and performs a real connection readiness check without
            // relying on /proc/net/tcp visibility.
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("pg_isready", "-U", "asterisk"));

        if (network is not null)
            builder = builder.WithNetwork(network).WithNetworkAliases("postgres");

        _container = builder.Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
