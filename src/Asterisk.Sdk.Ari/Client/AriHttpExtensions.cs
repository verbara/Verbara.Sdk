using System.Net;

namespace Asterisk.Sdk.Ari.Client;

internal static class AriHttpExtensions
{
    /// <summary>
    /// Throws the appropriate <see cref="AriException"/> for a non-success response.
    /// 404 -> <see cref="AriNotFoundException"/>, 409 -> <see cref="AriConflictException"/>, other -> <see cref="AriException"/>.
    /// </summary>
    public static ValueTask EnsureAriSuccessAsync(this HttpResponseMessage response)
        => response.EnsureAriSuccessAsync(resource: null, id: null);

    /// <summary>
    /// Throws the appropriate <see cref="AriException"/> for a non-success response, enriching
    /// the exception message with the resource name and (optional) entity id for easier debugging.
    /// </summary>
    /// <param name="response">The HTTP response to inspect.</param>
    /// <param name="resource">Logical resource name (e.g. <c>"channel"</c>, <c>"bridge"</c>).</param>
    /// <param name="id">Entity id when the call targets a specific resource instance; <c>null</c> for collection-level operations.</param>
    public static async ValueTask EnsureAriSuccessAsync(
        this HttpResponseMessage response,
        string? resource,
        string? id)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        var subject = BuildSubject(resource, id);

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new AriNotFoundException(
                subject is null ? body : $"{subject} not found{Suffix(body)}"),
            HttpStatusCode.Conflict => new AriConflictException(
                subject is null ? body : $"{subject} conflict{Suffix(body)}"),
            _ => new AriException(
                subject is null
                    ? $"ARI request failed with {(int)response.StatusCode}: {body}"
                    : $"ARI {subject} request failed with {(int)response.StatusCode}{Suffix(body)}",
                (int)response.StatusCode)
        };
    }

    private static string? BuildSubject(string? resource, string? id)
    {
        if (string.IsNullOrEmpty(resource)) return null;
        return string.IsNullOrEmpty(id) ? Capitalize(resource) : $"{Capitalize(resource)} '{id}'";
    }

    private static string Suffix(string body)
        => string.IsNullOrWhiteSpace(body) ? string.Empty : $": {body}";

    private static string Capitalize(string value)
        => char.IsUpper(value[0]) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
