using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ari.Outbound;

/// <summary>
/// Configuration options for <see cref="AriOutboundListener"/>.
/// Asterisk 22.5+ supports ARI Outbound WebSockets — the PBX initiates the
/// connection to the consumer application (<c>application=outbound</c> in
/// <c>ari.conf</c> + <c>res_websocket_client.so</c>). This listener accepts
/// those connections.
/// </summary>
public sealed class AriOutboundListenerOptions
{
    /// <summary>TCP port the listener binds to. Default: 8088 (mirrors the common ARI HTTP port).</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 8088;

    /// <summary>Local IP address the listener binds to. Default: <c>0.0.0.0</c>.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>URL path Asterisk will POST the upgrade request to. Default: <c>/ari/events</c>.</summary>
    [RegularExpression("^/.*$", ErrorMessage = "Path must start with '/'.")]
    public string Path { get; set; } = "/ari/events";

    /// <summary>
    /// Set of Stasis application names allowed to connect. When empty (default),
    /// all applications are allowed. The application name is read from the
    /// <c>?app=&lt;name&gt;</c> query string on the upgrade request.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Options pattern requires init/set for IOptions binding.")]
    public HashSet<string> AllowedApplications { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Expected Basic-Auth username Asterisk must send. When null, auth is not enforced.</summary>
    public string? ExpectedUsername { get; set; }

    /// <summary>Expected Basic-Auth password Asterisk must send. When null, auth is not enforced.</summary>
    public string? ExpectedPassword { get; set; }

    /// <summary>
    /// Idle timeout after which an accepted connection that sends no frames is closed.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan ConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// AOT-safe source-generated validator for <see cref="AriOutboundListenerOptions"/>.
/// Replaces reflection-based <c>ValidateDataAnnotations()</c> to avoid IL2026 trim warnings.
/// </summary>
[OptionsValidator]
public partial class AriOutboundListenerOptionsValidator : IValidateOptions<AriOutboundListenerOptions>
{
}
