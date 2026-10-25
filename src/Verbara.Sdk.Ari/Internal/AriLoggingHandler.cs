using System.Diagnostics;
using Verbara.Sdk.Ari.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Ari.Internal;

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
/// DelegatingHandler that logs, traces and meters all ARI HTTP requests and responses.
/// The creator assigns <see cref="DelegatingHandler.InnerHandler"/>, which disposing this handler disposes.
/// </summary>
internal sealed class AriLoggingHandler(ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var url = request.RequestUri?.PathAndQuery;

        using var activity = AriActivitySource.StartRequest(method, url);
        AriHttpLog.RequestSending(logger, method, url);

        // Counted when issued, so a request that then fails at the transport still counts; the roundtrip is
        // recorded only for a request that receives a response, whatever its status. Neither depends on the logger.
        AriMetrics.RestRequestsSent.Add(1);
        var sw = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        sw.Stop();
        AriMetrics.RestRoundtripMs.Record(sw.Elapsed.TotalMilliseconds);

        var statusCode = (int)response.StatusCode;
        AriActivitySource.SetResponse(activity, statusCode);

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
