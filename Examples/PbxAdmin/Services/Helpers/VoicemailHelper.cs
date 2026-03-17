using PbxAdmin.Models;

namespace PbxAdmin.Services.Helpers;

internal sealed class VoicemailInfo
{
    public string Pin { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Email { get; set; }
    public int MaxMessages { get; set; } = 50;
}

internal static partial class VoicemailLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[VOICEMAIL] Create: server={ServerId} ext={Extension}")]
    public static partial void Create(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[VOICEMAIL] Update: server={ServerId} ext={Extension}")]
    public static partial void Update(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[VOICEMAIL] Delete: server={ServerId} ext={Extension}")]
    public static partial void Delete(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[VOICEMAIL] Get: server={ServerId} ext={Extension}")]
    public static partial void Get(ILogger logger, string serverId, string extension);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[VOICEMAIL] Delete failed (best-effort): server={ServerId} ext={Extension}")]
    public static partial void DeleteFailed(ILogger logger, Exception exception, string serverId, string extension);
}

internal sealed class VoicemailHelper
{
    private const string ConfigFile = "voicemail.conf";
    private const string Section = "default";

    private readonly IConfigProviderResolver _resolver;
    private readonly ILogger<VoicemailHelper> _logger;

    public VoicemailHelper(IConfigProviderResolver resolver, ILogger<VoicemailHelper> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Creates a voicemail entry in voicemail.conf [default] if VoicemailEnabled is set.
    /// Uses UpdateSectionAsync because the [default] section already exists.
    /// </summary>
    public async Task CreateAsync(
        string serverId,
        string extension,
        ExtensionConfig config,
        CancellationToken ct = default)
    {
        if (!config.VoicemailEnabled)
            return;

        VoicemailLog.Create(_logger, serverId, extension);

        var pin = config.VoicemailPin ?? extension;
        var fullName = config.Name ?? extension;
        var value = FormatVoicemailValue(pin, fullName, config.VoicemailEmail, config.VoicemailMaxMessages);

        var provider = _resolver.GetProvider(serverId);
        await provider.UpdateSectionAsync(
            serverId,
            ConfigFile,
            Section,
            new Dictionary<string, string> { [extension] = value },
            ct);
    }

    /// <summary>
    /// Updates a voicemail entry by deleting the old value and writing the new one.
    /// </summary>
    public async Task UpdateAsync(
        string serverId,
        string extension,
        ExtensionConfig config,
        CancellationToken ct = default)
    {
        VoicemailLog.Update(_logger, serverId, extension);

        await DeleteAsync(serverId, extension, ct);
        await CreateAsync(serverId, extension, config, ct);
    }

    /// <summary>
    /// Removes a voicemail entry. Best-effort: logs a warning on failure but does not throw.
    /// </summary>
    public async Task DeleteAsync(string serverId, string extension, CancellationToken ct = default)
    {
        VoicemailLog.Delete(_logger, serverId, extension);

        try
        {
            var provider = _resolver.GetProvider(serverId);
            await provider.DeleteSectionAsync(serverId, ConfigFile, $"{Section}/{extension}", ct);
        }
        catch (Exception ex)
        {
            VoicemailLog.DeleteFailed(_logger, ex, serverId, extension);
        }
    }

    /// <summary>
    /// Reads and parses the voicemail entry for an extension.
    /// Returns null if not found or the value cannot be parsed.
    /// </summary>
    public async Task<VoicemailInfo?> GetAsync(string serverId, string extension, CancellationToken ct = default)
    {
        VoicemailLog.Get(_logger, serverId, extension);

        var provider = _resolver.GetProvider(serverId);
        var section = await provider.GetSectionAsync(serverId, ConfigFile, Section, ct);

        if (section is null || !section.TryGetValue(extension, out var value))
            return null;

        return ParseVoicemailValue(value);
    }

    // -----------------------------------------------------------------------
    // Static helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Formats a voicemail.conf value string.
    /// Format: pin,fullName,email,,attach=yes|maxmsg=N
    /// </summary>
    public static string FormatVoicemailValue(string pin, string fullName, string? email, int maxMessages)
    {
        var emailPart = email ?? "";
        return $"{pin},{fullName},{emailPart},,attach=yes|maxmsg={maxMessages}";
    }

    /// <summary>
    /// Parses a voicemail.conf value string into a <see cref="VoicemailInfo"/>.
    /// Returns null for null or empty input.
    /// </summary>
    public static VoicemailInfo? ParseVoicemailValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // Format: pin,fullName,email,pager,options
        var parts = value.Split(',');

        var info = new VoicemailInfo
        {
            Pin = parts.Length > 0 ? parts[0].Trim() : "",
            FullName = parts.Length > 1 ? parts[1].Trim() : "",
            Email = parts.Length > 2 ? (string.IsNullOrWhiteSpace(parts[2]) ? null : parts[2].Trim()) : null,
            MaxMessages = 50,
        };

        // Parse options from index 4 (after pager at index 3): attach=yes|maxmsg=N
        if (parts.Length > 4)
        {
            var options = parts[4].Trim();
            foreach (var opt in options.Split('|'))
            {
                var eqIdx = opt.IndexOf('=', StringComparison.Ordinal);
                if (eqIdx < 0)
                    continue;

                var key = opt[..eqIdx].Trim();
                var val = opt[(eqIdx + 1)..].Trim();

                if (string.Equals(key, "maxmsg", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(val, out var maxMsg))
                {
                    info.MaxMessages = maxMsg;
                }
            }
        }

        return info;
    }
}
