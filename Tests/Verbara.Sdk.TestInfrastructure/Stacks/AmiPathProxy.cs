using System.Globalization;
using System.Text;
using Verbara.Sdk.TestInfrastructure.Containers;

namespace Verbara.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// A Toxiproxy hop in front of one server's AMI port, on the two-server fixture's network: a host that connects to
/// <see cref="Address"/>:<see cref="Port"/> reaches the server through it, and <see cref="CutAsync"/> /
/// <see cref="RestoreAsync"/> cut and restore that path while the server keeps its own address.
/// </summary>
/// <remarks>
/// The cut is a <c>reset_peer</c> toxic with no delay: the established connection is reset and every new one is reset
/// as it opens, until <see cref="RestoreAsync"/> removes the toxic. Created and removed by
/// <see cref="MultiServerFixture.StartAmiPathProxyAsync"/> and the fixture's disposal.
/// </remarks>
public sealed class AmiPathProxy
{
    /// <summary>The port the proxy listens on inside its container.</summary>
    public const int Port = 15038;

    private const string ProxyName = "ami";
    private const string ToxicName = "ami-cut";

    private static readonly HttpClient Http = new();

    private readonly ToxiproxyContainer _container;

    internal AmiPathProxy(ToxiproxyContainer container)
    {
        _container = container;
    }

    /// <summary>The proxy's address on the fixture's network.</summary>
    public string Address => _container.NetworkAddress;

    private string ApiUrl => string.Create(CultureInfo.InvariantCulture, $"http://{_container.Host}:{_container.ApiPort}");

    /// <summary>Creates the proxy from <see cref="Port"/> to <paramref name="upstream"/> (<c>host:port</c> on the network).</summary>
    internal async Task CreateAsync(string upstream, CancellationToken cancellationToken)
    {
        var body = $"{{\"name\":\"{ProxyName}\",\"listen\":\"0.0.0.0:{Port}\",\"upstream\":\"{upstream}\",\"enabled\":true}}";
        await PostAsync("/proxies", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cuts the path: resets the established connection and every new one until <see cref="RestoreAsync"/>.</summary>
    public Task CutAsync(CancellationToken cancellationToken = default) =>
        PostAsync($"/proxies/{ProxyName}/toxics",
            $"{{\"name\":\"{ToxicName}\",\"type\":\"reset_peer\",\"stream\":\"downstream\",\"toxicity\":1.0,\"attributes\":{{\"timeout\":0}}}}",
            cancellationToken);

    /// <summary>Restores the path; a path that is not cut is left as it is.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.DeleteAsync(new Uri($"{ApiUrl}/proxies/{ProxyName}/toxics/{ToxicName}"), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    private async Task PostAsync(string path, string body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(new Uri(ApiUrl + path), content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
