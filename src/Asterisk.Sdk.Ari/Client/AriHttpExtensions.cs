using System.Net;

namespace Asterisk.Sdk.Ari.Client;

internal static class AriHttpExtensions
{
    public static async ValueTask EnsureAriSuccessAsync(this HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new AriNotFoundException(body),
            HttpStatusCode.Conflict => new AriConflictException(body),
            _ => new AriException($"ARI request failed with {(int)response.StatusCode}: {body}", (int)response.StatusCode)
        };
    }
}
