using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using PbxAdmin.Models;
using PbxAdmin.Services.Helpers;

namespace PbxAdmin.Services;

internal static partial class ExtensionServiceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[EXT] Status merge failed: server={ServerId}")]
    public static partial void MergeStatusFailed(ILogger logger, Exception exception, string serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[EXT] Created: server={ServerId} ext={Extension} tech={Technology}")]
    public static partial void Created(ILogger logger, string serverId, string extension, ExtensionTechnology technology);

    [LoggerMessage(Level = LogLevel.Information, Message = "[EXT] Deleted: server={ServerId} ext={Extension} tech={Technology}")]
    public static partial void Deleted(ILogger logger, string serverId, string extension, ExtensionTechnology technology);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[EXT] Cleanup failed for {Extension} on {ServerId}: {Detail}")]
    public static partial void CleanupFailed(ILogger logger, string extension, string serverId, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[EXT] Module reload failed after creating extension {Extension}")]
    public static partial void ReloadFailedCreate(ILogger logger, string extension);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[EXT] Module reload failed after updating extension {Extension}")]
    public static partial void ReloadFailedUpdate(ILogger logger, string extension);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[EXT] Module reload failed after deleting extension {Extension}")]
    public static partial void ReloadFailedDelete(ILogger logger, string extension);
}

/// <summary>
/// CRUD service for Asterisk extensions (PJSIP, SIP, IAX2).
/// </summary>
public sealed class ExtensionService : IExtensionService
{
    private readonly IConfigProviderResolver _resolver;
    private readonly AsteriskMonitorService _monitor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExtensionService> _logger;
    private readonly VoicemailHelper _voicemail;
    private readonly DeviceFeatureHelper _features;

    public ExtensionService(
        IConfigProviderResolver resolver,
        AsteriskMonitorService monitor,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _resolver = resolver;
        _monitor = monitor;
        _configuration = configuration;
        _logger = loggerFactory.CreateLogger<ExtensionService>();
        _voicemail = new VoicemailHelper(resolver, loggerFactory.CreateLogger<VoicemailHelper>());
        _features = new DeviceFeatureHelper(monitor, loggerFactory.CreateLogger<DeviceFeatureHelper>());
    }

    // -----------------------------------------------------------------------
    // Static helpers
    // -----------------------------------------------------------------------

    public static string GetConfigFilename(ExtensionTechnology technology) => technology switch
    {
        ExtensionTechnology.PjSip => "pjsip.conf",
        ExtensionTechnology.Sip => "sip.conf",
        ExtensionTechnology.Iax2 => "iax.conf",
        _ => "pjsip.conf"
    };

    public static string GetReloadModule(ExtensionTechnology technology) => technology switch
    {
        ExtensionTechnology.PjSip => "res_pjsip.so",
        ExtensionTechnology.Sip => "chan_sip.so",
        ExtensionTechnology.Iax2 => "chan_iax2.so",
        _ => "res_pjsip.so"
    };

    /// <summary>
    /// Reads the extension range for a server from configuration.
    /// Default: (100, 999).
    /// </summary>
    public static (int Start, int End) GetExtensionRange(IConfiguration config, string serverId)
    {
        var servers = config.GetSection("Asterisk:Servers").GetChildren();
        foreach (var section in servers)
        {
            var id = section["Id"] ?? section["ServerId"] ?? "";
            if (!string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))
                continue;

            var startStr = section["ExtensionRange:Start"];
            var endStr = section["ExtensionRange:End"];

            var start = int.TryParse(startStr, out var s) ? s : 100;
            var end = int.TryParse(endStr, out var e) ? e : 999;
            return (start, end);
        }

        return (100, 999);
    }

    /// <summary>Gets extension range for a server (instance convenience method).</summary>
    public (int Start, int End) GetExtensionRange(string serverId) => GetExtensionRange(_configuration, serverId);

    /// <summary>
    /// Returns true if the name is numeric and within the given extension range (inclusive).
    /// </summary>
    public static bool IsInExtensionRange(string name, int rangeStart, int rangeEnd)
    {
        return int.TryParse(name, out var num) && num >= rangeStart && num <= rangeEnd;
    }

    // -----------------------------------------------------------------------
    // Device features (public wrapper for inline editing)
    // -----------------------------------------------------------------------

    /// <summary>Sets device features (DND, CF, CFB, CFNA) for an extension via AstDB.</summary>
    public Task SetDeviceFeaturesAsync(
        string serverId, string extension, bool dnd,
        string? cfUnconditional, string? cfBusy, string? cfNoAnswer, int cfnaTimeout,
        CancellationToken ct = default)
    {
        var features = new DeviceFeatures(dnd, cfUnconditional, cfBusy, cfNoAnswer, cfnaTimeout);
        return _features.SetAsync(serverId, extension, features, ct);
    }

    // -----------------------------------------------------------------------
    // CRUD operations
    // -----------------------------------------------------------------------

    /// <summary>Gets all extensions from config files, merged with live status and features.</summary>
    public async Task<List<ExtensionViewModel>> GetExtensionsAsync(string serverId, CancellationToken ct = default)
    {
        var extensions = new List<ExtensionViewModel>();
        var configProvider = _resolver.GetProvider(serverId);
        var (rangeStart, rangeEnd) = GetExtensionRange(_configuration, serverId);

        // Load PJSIP extensions (type=endpoint, numeric names in range)
        var pjsipCategories = await configProvider.GetCategoriesAsync(serverId, "pjsip.conf", ct);
        foreach (var cat in pjsipCategories)
        {
            if (cat.Variables.GetValueOrDefault("type") != "endpoint")
                continue;
            if (!IsInExtensionRange(cat.Name, rangeStart, rangeEnd))
                continue;

            extensions.Add(new ExtensionViewModel
            {
                Extension = cat.Name,
                Name = ExtractCallerIdName(cat.Variables.GetValueOrDefault("callerid")),
                Technology = ExtensionTechnology.PjSip,
                Status = ExtensionStatus.Unknown,
            });
        }

        // Load SIP extensions (type=friend or peer, numeric names in range)
        var sipCategories = await configProvider.GetCategoriesAsync(serverId, "sip.conf", ct);
        foreach (var cat in sipCategories)
        {
            var type = cat.Variables.GetValueOrDefault("type");
            if (type is not ("friend" or "peer") || cat.Name == "general")
                continue;
            if (!IsInExtensionRange(cat.Name, rangeStart, rangeEnd))
                continue;

            extensions.Add(new ExtensionViewModel
            {
                Extension = cat.Name,
                Name = ExtractCallerIdName(cat.Variables.GetValueOrDefault("callerid")),
                Technology = ExtensionTechnology.Sip,
                Status = ExtensionStatus.Unknown,
            });
        }

        // Load IAX2 extensions (type=friend or peer, numeric names in range)
        var iaxCategories = await configProvider.GetCategoriesAsync(serverId, "iax.conf", ct);
        foreach (var cat in iaxCategories)
        {
            var type = cat.Variables.GetValueOrDefault("type");
            if (type is not ("friend" or "peer") || cat.Name == "general")
                continue;
            if (!IsInExtensionRange(cat.Name, rangeStart, rangeEnd))
                continue;

            extensions.Add(new ExtensionViewModel
            {
                Extension = cat.Name,
                Name = ExtractCallerIdName(cat.Variables.GetValueOrDefault("callerid")),
                Technology = ExtensionTechnology.Iax2,
                Status = ExtensionStatus.Unknown,
            });
        }

        // Merge live status via CLI
        await MergeStatusAsync(serverId, extensions, ct);

        // Batch enrich with DND/CF from DeviceFeatureHelper
        var extNames = extensions.Select(e => e.Extension).ToList();
        var featureMap = await _features.GetBatchAsync(serverId, extNames, ct);
        foreach (var ext in extensions)
        {
            if (featureMap.TryGetValue(ext.Extension, out var features))
            {
                ext.DndEnabled = features.Dnd;
                ext.CallForwardTo = features.CfUnconditional;
            }
        }

        // Check voicemail status from voicemail.conf
        var vmSection = await configProvider.GetSectionAsync(serverId, "voicemail.conf", "default", ct);
        if (vmSection is not null)
        {
            foreach (var ext in extensions)
            {
                ext.VoicemailEnabled = vmSection.ContainsKey(ext.Extension);
            }
        }

        return extensions;
    }

    /// <summary>Gets detailed extension information including config, status, features, and voicemail.</summary>
    public async Task<ExtensionDetailViewModel?> GetExtensionDetailAsync(
        string serverId, string extension, ExtensionTechnology technology, CancellationToken ct = default)
    {
        var config = await LoadExtensionConfigAsync(serverId, extension, technology, ct);
        if (config is null) return null;

        var vm = new ExtensionDetailViewModel
        {
            Extension = config.Extension,
            Name = config.Name,
            Technology = config.Technology,
            Context = config.Context,
            CallGroup = config.CallGroup,
            PickupGroup = config.PickupGroup,
            Codecs = config.Codecs,
            Status = ExtensionStatus.Unknown,
        };

        // Get PJSIP detailed status
        if (technology == ExtensionTechnology.PjSip)
        {
            var output = await _resolver.GetProvider(serverId)
                .ExecuteCommandAsync(serverId, $"pjsip show endpoint {extension}", ct);
            if (output is not null)
            {
                vm.ContactUri = ExtractField(output, "Contact:");
                vm.UserAgent = ExtractField(output, "UserAgent:");
                vm.IpAddress = ExtractIpFromContact(vm.ContactUri);
                vm.RoundtripMs = ExtractRoundtrip(output);
                vm.Status = DetectPjsipStatus(output);
            }
        }

        // Get device features
        var features = await _features.GetAsync(serverId, extension, ct);
        vm.DndEnabled = features.Dnd;
        vm.CallForwardTo = features.CfUnconditional;
        vm.CfBusy = features.CfBusy;
        vm.CfNoAnswer = features.CfNoAnswer;
        vm.CfNoAnswerTimeout = features.CfnaTimeout;

        // Get voicemail info + message count
        var vmInfo = await _voicemail.GetAsync(serverId, extension, ct);
        if (vmInfo is not null)
        {
            vm.VoicemailEnabled = true;
            vm.VoicemailEmail = vmInfo.Email;

            try
            {
                var entry = _monitor.GetServer(serverId);
                if (entry is not null)
                {
                    var response = await entry.ConfigConnection.SendActionAsync<MailboxCountResponse>(
                        new MailboxCountAction { Mailbox = $"{extension}@default" }, ct);
                    vm.VoicemailMessages = (response.NewMessages ?? 0) + (response.OldMessages ?? 0);
                }
            }
            catch
            {
                // Mailbox count is best-effort
            }
        }

        // Load raw config sections
        vm.RawConfig = await LoadRawConfigAsync(serverId, extension, technology, ct);

        return vm;
    }

    /// <summary>Creates an extension with all required config sections, voicemail, and features.</summary>
    public async Task<bool> CreateExtensionAsync(string serverId, ExtensionConfig config, CancellationToken ct = default)
    {
        // Validate: range
        var (rangeStart, rangeEnd) = GetExtensionRange(_configuration, serverId);
        if (!IsInExtensionRange(config.Extension, rangeStart, rangeEnd))
            return false;

        // Validate: uniqueness
        if (await ExtensionExistsAsync(serverId, config.Extension, ct))
            return false;

        // Validate: password >= 8
        if (string.IsNullOrEmpty(config.Password) || config.Password.Length < 8)
            return false;

        // Validate: codecs not empty
        if (string.IsNullOrWhiteSpace(config.Codecs))
            return false;

        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(config.Technology);
        bool success;

        if (config.Technology == ExtensionTechnology.PjSip)
        {
            // Create 3 PJSIP sections: endpoint, auth, aor
            success = await configProvider.CreateSectionAsync(serverId, filename, config.Extension, config.ToPjsipEndpoint(), ct: ct);
            if (!success) return false;

            success = await configProvider.CreateSectionAsync(serverId, filename, $"{config.Extension}-auth", config.ToPjsipAuth(), ct: ct);
            if (!success) return false;

            success = await configProvider.CreateSectionAsync(serverId, filename, $"{config.Extension}-aor", config.ToPjsipAor(), ct: ct);
            if (!success) return false;
        }
        else
        {
            var vars = config.Technology == ExtensionTechnology.Sip ? config.ToSipPeer() : config.ToIaxPeer();
            success = await configProvider.CreateSectionAsync(serverId, filename, config.Extension, vars, ct: ct);
            if (!success) return false;
        }

        // Create voicemail if enabled
        await _voicemail.CreateAsync(serverId, config.Extension, config, ct);

        // Set device features if any
        var features = new DeviceFeatures(
            config.DndEnabled,
            config.CallForwardUnconditional,
            config.CallForwardBusy,
            config.CallForwardNoAnswer,
            config.CallForwardNoAnswerTimeout);
        if (features != DeviceFeatures.Empty)
        {
            await _features.SetAsync(serverId, config.Extension, features, ct);
        }

        // Reload module
        if (!await configProvider.ReloadModuleAsync(serverId, GetReloadModule(config.Technology), ct))
            ExtensionServiceLog.ReloadFailedCreate(_logger, config.Extension);

        ExtensionServiceLog.Created(_logger, serverId, config.Extension, config.Technology);
        return true;
    }

    /// <summary>Updates an extension by preserving password if not provided, then delete + create.</summary>
    public async Task<bool> UpdateExtensionAsync(string serverId, string extension, ExtensionConfig config, CancellationToken ct = default)
    {
        // If password null/empty, preserve existing (read from config before delete)
        if (string.IsNullOrEmpty(config.Password))
        {
            var existing = await LoadExtensionConfigAsync(serverId, extension, config.Technology, ct);
            if (existing is not null && !string.IsNullOrEmpty(existing.Password))
            {
                config.Password = existing.Password;
            }
        }

        // Delete + Create pattern
        if (!await DeleteSectionsAsync(serverId, extension, config.Technology, ct))
            return false;

        // For update, remove uniqueness check by creating directly
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(config.Technology);
        bool success;

        if (config.Technology == ExtensionTechnology.PjSip)
        {
            success = await configProvider.CreateSectionAsync(serverId, filename, config.Extension, config.ToPjsipEndpoint(), ct: ct);
            if (!success) return false;

            success = await configProvider.CreateSectionAsync(serverId, filename, $"{config.Extension}-auth", config.ToPjsipAuth(), ct: ct);
            if (!success) return false;

            success = await configProvider.CreateSectionAsync(serverId, filename, $"{config.Extension}-aor", config.ToPjsipAor(), ct: ct);
            if (!success) return false;
        }
        else
        {
            var vars = config.Technology == ExtensionTechnology.Sip ? config.ToSipPeer() : config.ToIaxPeer();
            success = await configProvider.CreateSectionAsync(serverId, filename, config.Extension, vars, ct: ct);
            if (!success) return false;
        }

        // Update voicemail
        await _voicemail.UpdateAsync(serverId, config.Extension, config, ct);

        // Update device features
        var features = new DeviceFeatures(
            config.DndEnabled,
            config.CallForwardUnconditional,
            config.CallForwardBusy,
            config.CallForwardNoAnswer,
            config.CallForwardNoAnswerTimeout);
        await _features.SetAsync(serverId, config.Extension, features, ct);

        // Reload module
        if (!await configProvider.ReloadModuleAsync(serverId, GetReloadModule(config.Technology), ct))
            ExtensionServiceLog.ReloadFailedUpdate(_logger, config.Extension);

        ExtensionServiceLog.Created(_logger, serverId, config.Extension, config.Technology);
        return true;
    }

    /// <summary>Deletes an extension and all associated config, voicemail, and AstDB entries.</summary>
    public async Task<bool> DeleteExtensionAsync(
        string serverId, string extension, ExtensionTechnology technology, CancellationToken ct = default)
    {
        if (!await DeleteSectionsAsync(serverId, extension, technology, ct))
            return false;

        // Best-effort voicemail cleanup
        try
        {
            await _voicemail.DeleteAsync(serverId, extension, ct);
        }
        catch (Exception ex)
        {
            ExtensionServiceLog.CleanupFailed(_logger, extension, serverId, $"voicemail: {ex.Message}");
        }

        // Best-effort AstDB cleanup
        try
        {
            await _features.CleanupAsync(serverId, extension, ct);
        }
        catch (Exception ex)
        {
            ExtensionServiceLog.CleanupFailed(_logger, extension, serverId, $"astdb: {ex.Message}");
        }

        // Reload module
        if (!await _resolver.GetProvider(serverId).ReloadModuleAsync(serverId, GetReloadModule(technology), ct))
            ExtensionServiceLog.ReloadFailedDelete(_logger, extension);

        ExtensionServiceLog.Deleted(_logger, serverId, extension, technology);
        return true;
    }

    /// <summary>Checks if an extension exists in any config file.</summary>
    public async Task<bool> ExtensionExistsAsync(string serverId, string extension, CancellationToken ct = default)
    {
        var configProvider = _resolver.GetProvider(serverId);

        // Check pjsip.conf
        var pjsipSection = await configProvider.GetSectionAsync(serverId, "pjsip.conf", extension, ct);
        if (pjsipSection is not null) return true;

        // Check sip.conf
        var sipSection = await configProvider.GetSectionAsync(serverId, "sip.conf", extension, ct);
        if (sipSection is not null) return true;

        // Check iax.conf
        var iaxSection = await configProvider.GetSectionAsync(serverId, "iax.conf", extension, ct);
        if (iaxSection is not null) return true;

        return false;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task<bool> DeleteSectionsAsync(
        string serverId, string extension, ExtensionTechnology technology, CancellationToken ct)
    {
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(technology);

        if (technology == ExtensionTechnology.PjSip)
        {
            // Delete all 3 PJSIP sections: endpoint, auth, aor
            await configProvider.DeleteSectionAsync(serverId, filename, extension, ct);
            await configProvider.DeleteSectionAsync(serverId, filename, $"{extension}-auth", ct);
            await configProvider.DeleteSectionAsync(serverId, filename, $"{extension}-aor", ct);
            return true;
        }

        return await configProvider.DeleteSectionAsync(serverId, filename, extension, ct);
    }

    private async Task<ExtensionConfig?> LoadExtensionConfigAsync(
        string serverId, string extension, ExtensionTechnology technology, CancellationToken ct)
    {
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(technology);

        if (technology == ExtensionTechnology.PjSip)
        {
            var categories = await configProvider.GetCategoriesAsync(serverId, filename, ct);
            var catDict = categories.ToDictionary(c => c.Name, c => c.Variables, StringComparer.OrdinalIgnoreCase);

            catDict.TryGetValue(extension, out var endpoint);
            catDict.TryGetValue($"{extension}-auth", out var auth);
            catDict.TryGetValue($"{extension}-aor", out var aor);

            if (endpoint is null) return null;

            return ExtensionConfig.FromPjsipSections(extension, endpoint, auth, aor);
        }

        var section = await configProvider.GetSectionAsync(serverId, filename, extension, ct);
        if (section is null) return null;

        return technology == ExtensionTechnology.Sip
            ? ExtensionConfig.FromSipPeer(extension, section)
            : ExtensionConfig.FromIaxPeer(extension, section);
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> LoadRawConfigAsync(
        string serverId, string extension, ExtensionTechnology technology, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(technology);

        if (technology == ExtensionTechnology.PjSip)
        {
            var endpoint = await configProvider.GetSectionAsync(serverId, filename, extension, ct);
            if (endpoint is not null) result[extension] = endpoint;

            var auth = await configProvider.GetSectionAsync(serverId, filename, $"{extension}-auth", ct);
            if (auth is not null) result[$"{extension}-auth"] = auth;

            var aor = await configProvider.GetSectionAsync(serverId, filename, $"{extension}-aor", ct);
            if (aor is not null) result[$"{extension}-aor"] = aor;
        }
        else
        {
            var section = await configProvider.GetSectionAsync(serverId, filename, extension, ct);
            if (section is not null) result[extension] = section;
        }

        return result;
    }

    private async Task MergeStatusAsync(string serverId, List<ExtensionViewModel> extensions, CancellationToken ct)
    {
        try
        {
            var configProvider = _resolver.GetProvider(serverId);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            // Get PJSIP endpoint statuses
            var pjsipOutput = await configProvider.ExecuteCommandAsync(serverId, "pjsip show endpoints", cts.Token);
            if (pjsipOutput is not null)
            {
                foreach (var ext in extensions.Where(e => e.Technology == ExtensionTechnology.PjSip))
                {
                    ext.Status = DetectStatusFromOutput(pjsipOutput, ext.Extension);
                }
            }

            // Get SIP peer statuses
            var sipOutput = await configProvider.ExecuteCommandAsync(serverId, "sip show peers", cts.Token);
            if (sipOutput is not null)
            {
                foreach (var ext in extensions.Where(e => e.Technology == ExtensionTechnology.Sip))
                {
                    ext.Status = DetectStatusFromOutput(sipOutput, ext.Extension);
                }
            }

            // Get IAX2 peer statuses
            var iaxOutput = await configProvider.ExecuteCommandAsync(serverId, "iax2 show peers", cts.Token);
            if (iaxOutput is not null)
            {
                foreach (var ext in extensions.Where(e => e.Technology == ExtensionTechnology.Iax2))
                {
                    ext.Status = DetectStatusFromOutput(iaxOutput, ext.Extension);
                }
            }
        }
        catch (Exception ex)
        {
            ExtensionServiceLog.MergeStatusFailed(_logger, ex, serverId);
        }
    }

    /// <summary>
    /// Detects PJSIP endpoint status from "pjsip show endpoints" output.
    /// Searches for the Endpoint line matching the name and parses its state.
    /// States: "Not in use"=registered, "In use"=registered+call, "Unavailable"=offline.
    /// </summary>
    private static ExtensionStatus DetectStatusFromOutput(string output, string name)
    {
        foreach (var line in output.Split('\n'))
        {
            // Only match "Endpoint:" lines containing our name to avoid auth/aor false matches
            if (!line.Contains("Endpoint:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!line.Contains(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return ParseEndpointLine(line.ToUpperInvariant());
        }

        return ExtensionStatus.Unknown;
    }

    private static ExtensionStatus ParseEndpointLine(string upper)
    {
        // Check negative states first (UNAVAIL before AVAIL, UNREGISTERED before REGISTERED)
        if (upper.Contains("UNAVAIL") || upper.Contains("UNREACHABLE") || upper.Contains("UNREGISTERED"))
            return ExtensionStatus.Unregistered;

        // Positive states: "Not in use", "In use", "Ringing", "Busy" all mean registered
        if (upper.Contains("NOT IN USE") || upper.Contains("IN USE")
            || upper.Contains("RINGING") || upper.Contains("BUSY"))
            return ExtensionStatus.Registered;

        // Fallback positive checks
        if (upper.Contains("AVAIL") || upper.Contains("REACHABLE") || upper.Contains("REGISTERED"))
            return ExtensionStatus.Registered;

        return ExtensionStatus.Unknown;
    }

    /// <summary>
    /// Detects PJSIP status from contact/endpoint detail output.
    /// </summary>
    private static ExtensionStatus DetectPjsipStatus(string output)
    {
        var upper = output.ToUpperInvariant();
        // Check negative first to avoid AVAIL matching inside UNAVAIL
        if (upper.Contains("UNAVAIL") || upper.Contains("UNREACHABLE"))
            return ExtensionStatus.Unreachable;
        if (upper.Contains("NOT IN USE") || upper.Contains("IN USE")
            || upper.Contains("AVAIL") || upper.Contains("REACHABLE"))
            return ExtensionStatus.Registered;
        return ExtensionStatus.Unknown;
    }

    internal static string? ExtractField(string output, string fieldName)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[fieldName.Length..].Trim();
            }
        }
        return null;
    }

    internal static int? ExtractRoundtrip(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("RTT:", StringComparison.OrdinalIgnoreCase))
            {
                var rttIdx = trimmed.IndexOf("RTT:", StringComparison.OrdinalIgnoreCase) + 4;
                var rttStr = trimmed[rttIdx..].Trim().Split(' ', 'm')[0];
                if (int.TryParse(rttStr, out var ms))
                    return ms;
            }
        }
        return null;
    }

    internal static string? ExtractCallerIdName(string? callerid)
    {
        if (string.IsNullOrEmpty(callerid))
            return null;

        var startQuote = callerid.IndexOf('"');
        if (startQuote < 0) return null;
        var endQuote = callerid.IndexOf('"', startQuote + 1);
        if (endQuote < 0) return null;
        return callerid[(startQuote + 1)..endQuote];
    }

    internal static string? ExtractIpFromContact(string? contactUri)
    {
        if (string.IsNullOrEmpty(contactUri))
            return null;

        // Contact URI format: sip:user@ip:port or sip:ip:port
        var atIdx = contactUri.IndexOf('@');
        if (atIdx < 0) return null;

        var afterAt = contactUri[(atIdx + 1)..];
        // Remove port if present
        var colonIdx = afterAt.IndexOf(':');
        return colonIdx > 0 ? afterAt[..colonIdx] : afterAt;
    }
}
