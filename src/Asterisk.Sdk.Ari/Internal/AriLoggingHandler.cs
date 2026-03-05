using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Ari.Internal;

internal static partial class AriHttpLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[ARI_HTTP] Request: method={Method} url={Url}")]
    public static partial void RequestSending(ILogger logger, string method, string? url);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[ARI_HTTP] Response: method={Method} url={Url} status={StatusCode} elapsed_ms={ElapsedMs}")]
    public static partial void ResponseReceived(ILogger logger, string method, string? url, int statusCode, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ARI_HTTP] Error response: method={Method} url={Url} status={StatusCode} body={Body}")]
    public static partial void ErrorResponse(ILogger logger, string method, string? url, int statusCode, string? body);
}

/// <summary>
/// DelegatingHandler that logs all ARI HTTP requests and responses.
/// </summary>
internal sealed class AriLoggingHandler(ILogger logger) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var url = request.RequestUri?.PathAndQuery;

        AriHttpLog.RequestSending(logger, method, url);

        var sw = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        sw.Stop();

        var statusCode = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            AriHttpLog.ResponseReceived(logger, method, url, statusCode, sw.ElapsedMilliseconds);
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            AriHttpLog.ErrorResponse(logger, method, url, statusCode, body);
        }

        return response;
    }
}
