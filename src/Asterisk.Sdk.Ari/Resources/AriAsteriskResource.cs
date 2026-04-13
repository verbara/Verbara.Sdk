using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Asterisk resource - system info, modules, logging, config, variables.</summary>
public sealed class AriAsteriskResource : IAriAsteriskResource
{
    private readonly HttpClient _http;

    internal AriAsteriskResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriAsteriskInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("asterisk/info", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriAsteriskInfo)!;
    }

    public async ValueTask<AriAsteriskPing> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("asterisk/ping", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriAsteriskPing)!;
    }

    public async ValueTask<string> GetVariableAsync(string variable, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/variable?variable={Uri.EscapeDataString(variable)}";
        var response = await _http.GetAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("variable", variable);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriVariable)!;
        return result.Value;
    }

    public async ValueTask SetVariableAsync(string variable, string value, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/variable?variable={Uri.EscapeDataString(variable)}&value={Uri.EscapeDataString(value)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public async ValueTask<AriModule[]> ListModulesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("asterisk/modules", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriModuleArray) ?? [];
    }

    public async ValueTask<AriModule> GetModuleAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/modules/{Uri.EscapeDataString(moduleName)}";
        var response = await _http.GetAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("module", moduleName);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriModule)!;
    }

    public async ValueTask LoadModuleAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/modules/{Uri.EscapeDataString(moduleName)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync("module", moduleName);
    }

    public async ValueTask UnloadModuleAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/modules/{Uri.EscapeDataString(moduleName)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("module", moduleName);
    }

    public async ValueTask ReloadModuleAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/modules/{Uri.EscapeDataString(moduleName)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        var response = await _http.SendAsync(request, cancellationToken);
        await response.EnsureAriSuccessAsync("module", moduleName);
    }

    public async ValueTask<AriLogChannel[]> ListLoggingAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("asterisk/logging", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriLogChannelArray) ?? [];
    }

    public async ValueTask AddLogChannelAsync(string logChannelName, string configuration, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/logging/{Uri.EscapeDataString(logChannelName)}?configuration={Uri.EscapeDataString(configuration)}";
        var response = await _http.PostAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync("logChannel", logChannelName);
    }

    public async ValueTask DeleteLogChannelAsync(string logChannelName, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/logging/{Uri.EscapeDataString(logChannelName)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("logChannel", logChannelName);
    }

    public async ValueTask RotateLogChannelAsync(string logChannelName, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/logging/{Uri.EscapeDataString(logChannelName)}/rotate";
        var response = await _http.PostAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync("logChannel", logChannelName);
    }

    public async ValueTask<AriConfigTuple[]> GetConfigAsync(string configClass, string objectType, string id, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/config/dynamic/{Uri.EscapeDataString(configClass)}/{Uri.EscapeDataString(objectType)}/{Uri.EscapeDataString(id)}";
        var response = await _http.GetAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("config", id);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriConfigTupleArray) ?? [];
    }

    public async ValueTask UpdateConfigAsync(string configClass, string objectType, string id, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/config/dynamic/{Uri.EscapeDataString(configClass)}/{Uri.EscapeDataString(objectType)}/{Uri.EscapeDataString(id)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        var response = await _http.SendAsync(request, cancellationToken);
        await response.EnsureAriSuccessAsync("config", id);
    }

    public async ValueTask DeleteConfigAsync(string configClass, string objectType, string id, CancellationToken cancellationToken = default)
    {
        var url = $"asterisk/config/dynamic/{Uri.EscapeDataString(configClass)}/{Uri.EscapeDataString(objectType)}/{Uri.EscapeDataString(id)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("config", id);
    }
}
