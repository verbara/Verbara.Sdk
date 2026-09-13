namespace Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Net.Sockets;
using System.Text;

public static class ToxiproxyControl
{
    private static readonly HttpClient _http = new();

    public static string ApiUrl =>
        Environment.GetEnvironmentVariable("TOXIPROXY_API_URL") ?? "http://localhost:8474";

    public static string ProxyListenHost =>
        Environment.GetEnvironmentVariable("TOXIPROXY_HOST") ?? "localhost";

    public static int ProxyAmiPort => int.Parse(
        Environment.GetEnvironmentVariable("TOXIPROXY_PROXY_PORT") ?? "15038",
        System.Globalization.CultureInfo.InvariantCulture);

    // POST /proxies — create a proxy
    public static async Task CreateProxyAsync(string name, string listen, string upstream)
    {
        var body = $"{{\"name\":\"{Esc(name)}\",\"listen\":\"{Esc(listen)}\",\"upstream\":\"{Esc(upstream)}\",\"enabled\":true}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{ApiUrl}/proxies", content);
        response.EnsureSuccessStatusCode();
    }

    // POST /proxies/{name}/toxics — add a toxic
    public static async Task AddToxicAsync(string proxyName, string toxicName,
        string toxicType, string stream = "downstream",
        Dictionary<string, object>? attributes = null)
    {
        var attrs = BuildAttributesJson(attributes);
        var body = $"{{\"name\":\"{Esc(toxicName)}\",\"type\":\"{Esc(toxicType)}\",\"stream\":\"{Esc(stream)}\",\"toxicity\":1.0,\"attributes\":{attrs}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{ApiUrl}/proxies/{proxyName}/toxics", content);
        response.EnsureSuccessStatusCode();
    }

    // DELETE /proxies/{name}/toxics/{toxicName}
    public static async Task RemoveToxicAsync(string proxyName, string toxicName)
    {
        var response = await _http.DeleteAsync($"{ApiUrl}/proxies/{proxyName}/toxics/{toxicName}");
        response.EnsureSuccessStatusCode();
    }

    // Reset all proxies and toxics via POST /reset
    public static async Task ResetAsync()
    {
        var response = await _http.PostAsync($"{ApiUrl}/reset", content: null);
        response.EnsureSuccessStatusCode();
    }

    // DELETE /proxies/{name}/toxics/{toxicName} for cleanup. A toxic that is already gone (404)
    // counts as removed, which is the usual case in a finally once the test removed it itself.
    // Returns false instead of throwing when the API is unreachable or the request times out.
    public static async Task<bool> TryRemoveToxicAsync(string proxyName, string toxicName)
    {
        try
        {
            using var response = await _http.DeleteAsync($"{ApiUrl}/proxies/{proxyName}/toxics/{toxicName}");
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    // POST /reset for teardown. Returns false instead of throwing when the API is unreachable or
    // the request times out, as it is once the Toxiproxy container has been stopped.
    public static async Task<bool> TryResetAsync()
    {
        try
        {
            using var response = await _http.PostAsync($"{ApiUrl}/reset", content: null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    // Check if API is reachable (TCP check on 8474)
    public static bool IsAvailable()
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync("localhost", 8474).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            return false;
        }
    }

    // Escape a string for JSON (handles quotes and backslashes)
    private static string Esc(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string BuildAttributesJson(Dictionary<string, object>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return "{}";

        var sb = new StringBuilder("{");
        var first = true;
        foreach (var kv in attributes)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Esc(kv.Key)).Append("\":");
            var valStr = kv.Value switch
            {
                string s => "\"" + Esc(s) + "\"",
                bool b => b ? "true" : "false",
                _ => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "{0}", kv.Value)
            };
            sb.Append(valStr);
        }
        sb.Append('}');
        return sb.ToString();
    }
}
