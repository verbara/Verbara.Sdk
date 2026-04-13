using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;

namespace Asterisk.Sdk.Ari.Resources;

/// <summary>ARI Mailboxes resource - REST operations on mailboxes.</summary>
public sealed class AriMailboxesResource : IAriMailboxesResource
{
    private readonly HttpClient _http;

    internal AriMailboxesResource(HttpClient http)
    {
        _http = http;
    }

    public async ValueTask<AriMailbox[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync("mailboxes", cancellationToken);
        await response.EnsureAriSuccessAsync();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriMailboxArray) ?? [];
    }

    public async ValueTask<AriMailbox> GetAsync(string mailboxName, CancellationToken cancellationToken = default)
    {
        var url = $"mailboxes/{Uri.EscapeDataString(mailboxName)}";
        var response = await _http.GetAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("mailbox", mailboxName);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, AriJsonContext.Default.AriMailbox)!;
    }

    public async ValueTask UpdateAsync(string mailboxName, int oldMessages, int newMessages, CancellationToken cancellationToken = default)
    {
        var url = $"mailboxes/{Uri.EscapeDataString(mailboxName)}?oldMessages={oldMessages}&newMessages={newMessages}";
        var response = await _http.PutAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync("mailbox", mailboxName);
    }

    public async ValueTask DeleteAsync(string mailboxName, CancellationToken cancellationToken = default)
    {
        var url = $"mailboxes/{Uri.EscapeDataString(mailboxName)}";
        var response = await _http.DeleteAsync(url, cancellationToken);
        await response.EnsureAriSuccessAsync("mailbox", mailboxName);
    }
}
