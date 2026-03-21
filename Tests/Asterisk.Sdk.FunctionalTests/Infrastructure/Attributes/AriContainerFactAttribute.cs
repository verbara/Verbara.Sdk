namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

/// <summary>
/// Skips the test if the ARI HTTP endpoint (port 8088) is not reachable.
/// </summary>
public sealed class AriContainerFactAttribute : FactAttribute
{
    public AriContainerFactAttribute()
    {
        if (!IsAriReachable())
        {
            Skip = $"ARI endpoint not reachable at {AriClientFactory.Host}:{AriClientFactory.HttpPort}";
        }
    }

    private static bool IsAriReachable()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = client.GetAsync($"http://{AriClientFactory.Host}:{AriClientFactory.HttpPort}/ari/api-docs/resources.json")
                .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
    }
}
