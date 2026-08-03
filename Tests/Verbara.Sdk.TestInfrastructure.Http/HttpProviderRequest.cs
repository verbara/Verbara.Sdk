using WireMock.Matchers;
using WireMock.Matchers.Request;
using WireMock.RequestBuilders;
using WireMock.Types;

namespace Verbara.Sdk.TestInfrastructure.Http;

/// <summary>
/// A strict request expectation for <see cref="HttpProviderMockServer"/>: method, path, query and
/// the headers the provider requires.
/// </summary>
/// <remarks>
/// <para>
/// Strictness is the default and is what makes this substrate stronger than a canned
/// <c>HttpMessageHandler</c> (ADR-0041 D1): the method and path are matched with an
/// <see cref="ExactMatcher"/> (case-sensitive, no wildcards), every declared query parameter and
/// header must be present with the exact value, and any query parameter the expectation does NOT
/// declare makes the request fail to match. A client that posts to the wrong path, drops its
/// API-key header, or grows an unexpected query parameter therefore gets a 404 instead of the
/// canned success body.
/// </para>
/// <para>
/// Headers are matched by inclusion, not exhaustively — <see cref="HttpClient"/> always adds
/// <c>Host</c>, <c>Content-Length</c> and friends, so demanding an exact header set would assert
/// the transport rather than the provider contract. Query parameters have no such noise, which is
/// why they are exhaustive; <see cref="AllowingUndeclaredQueryParameters"/> is the explicit opt-out.
/// </para>
/// </remarks>
public sealed class HttpProviderRequest
{
    private readonly Dictionary<string, string> _query;
    private readonly Dictionary<string, string> _headers;

    private HttpProviderRequest(
        string method,
        string path,
        Dictionary<string, string> query,
        Dictionary<string, string> headers,
        bool allowsUndeclaredQueryParameters)
    {
        Method = method;
        Path = path;
        _query = query;
        _headers = headers;
        AllowsUndeclaredQueryParameters = allowsUndeclaredQueryParameters;
    }

    /// <summary>The HTTP method, upper-cased.</summary>
    public string Method { get; }

    /// <summary>The absolute request path, without query string.</summary>
    public string Path { get; }

    /// <summary>Query parameters that must be present with these exact values.</summary>
    public IReadOnlyDictionary<string, string> Query => _query;

    /// <summary>Headers that must be present with these exact values.</summary>
    public IReadOnlyDictionary<string, string> Headers => _headers;

    /// <summary>
    /// When <see langword="true"/>, query parameters beyond <see cref="Query"/> no longer break the
    /// match. Off by default — see <see cref="AllowingUndeclaredQueryParameters"/>.
    /// </summary>
    public bool AllowsUndeclaredQueryParameters { get; }

    /// <summary>Expect a <c>GET</c> to <paramref name="path"/>.</summary>
    public static HttpProviderRequest Get(string path) => For("GET", path);

    /// <summary>Expect a <c>POST</c> to <paramref name="path"/>.</summary>
    public static HttpProviderRequest Post(string path) => For("POST", path);

    /// <summary>Expect <paramref name="method"/> to <paramref name="path"/>.</summary>
    public static HttpProviderRequest For(string method, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new HttpProviderRequest(
            method.ToUpperInvariant(),
            path,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            allowsUndeclaredQueryParameters: false);
    }

    /// <summary>Require query parameter <paramref name="key"/> to equal <paramref name="value"/>.</summary>
    public HttpProviderRequest WithQuery(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var query = new Dictionary<string, string>(_query, StringComparer.OrdinalIgnoreCase) { [key] = value };
        return new HttpProviderRequest(Method, Path, query, _headers, AllowsUndeclaredQueryParameters);
    }

    /// <summary>Require header <paramref name="name"/> to equal <paramref name="value"/>.</summary>
    public HttpProviderRequest WithHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase) { [name] = value };
        return new HttpProviderRequest(Method, Path, _query, headers, AllowsUndeclaredQueryParameters);
    }

    /// <summary>Require <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    public HttpProviderRequest WithBearerToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return WithHeader("Authorization", "Bearer " + token);
    }

    /// <summary>
    /// Opt out of exhaustive query matching: undeclared query parameters stop breaking the match.
    /// Use it only where the client legitimately appends parameters the test does not pin.
    /// </summary>
    public HttpProviderRequest AllowingUndeclaredQueryParameters() =>
        new(Method, Path, _query, _headers, allowsUndeclaredQueryParameters: true);

    /// <summary>Translate the expectation into the WireMock matcher chain.</summary>
    internal IRequestMatcher BuildMatcher()
    {
        var builder = Request.Create()
            .UsingMethod(Method)
            .WithPath(new ExactMatcher(MatchBehaviour.AcceptOnMatch, Path));

        foreach (var (key, value) in _query)
        {
            builder = builder.WithParam(key, new ExactMatcher(MatchBehaviour.AcceptOnMatch, value));
        }

        if (!AllowsUndeclaredQueryParameters)
        {
            builder = builder.WithParam(DeclaresEveryQueryParameter);
        }

        foreach (var (name, value) in _headers)
        {
            builder = builder.WithHeader(name, new ExactMatcher(MatchBehaviour.AcceptOnMatch, value));
        }

        return builder;
    }

    private bool DeclaresEveryQueryParameter(IDictionary<string, WireMockList<string>> parameters)
    {
        if (parameters.Count != _query.Count)
            return false;

        foreach (var key in parameters.Keys)
        {
            if (!_query.ContainsKey(key))
                return false;
        }

        return true;
    }
}
