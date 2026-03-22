using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using PbxAdmin.Models;

namespace PbxAdmin.Services;

internal static partial class TrunkServiceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[TRUNK] Status merge failed: server={ServerId}")]
    public static partial void MergeStatusFailed(ILogger logger, Exception exception, string serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[TRUNK] Created: server={ServerId} name={Name} technology={Technology}")]
    public static partial void Created(ILogger logger, string serverId, string name, TrunkTechnology technology);

    [LoggerMessage(Level = LogLevel.Information, Message = "[TRUNK] Deleted: server={ServerId} name={Name} technology={Technology}")]
    public static partial void Deleted(ILogger logger, string serverId, string name, TrunkTechnology technology);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[TRUNK] Module reload failed after creating trunk {Name}")]
    public static partial void ReloadFailedCreate(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[TRUNK] Module reload failed after deleting trunk {Name}")]
    public static partial void ReloadFailedDelete(ILogger logger, string name);
}

/// <summary>
/// CRUD service for Asterisk trunks (PJSIP, SIP, IAX2).
/// </summary>
public sealed class TrunkService : ITrunkService
{
    private readonly IConfigProviderResolver _resolver;
    private readonly AsteriskMonitorService _monitor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TrunkService> _logger;

    public TrunkService(IConfigProviderResolver resolver, AsteriskMonitorService monitor, IConfiguration configuration, ILogger<TrunkService> logger)
    {
        _resolver = resolver;
        _monitor = monitor;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Gets all trunks from all config files, merged with live status.</summary>
    public async Task<List<TrunkViewModel>> GetTrunksAsync(string serverId, CancellationToken ct = default)
    {
        var trunks = new List<TrunkViewModel>();
        var range = ExtensionService.GetExtensionRange(_configuration, serverId);
        var configProvider = _resolver.GetProvider(serverId);

        await LoadPjsipTrunksAsync(serverId, configProvider, trunks, range, ct);
        await LoadSimpleTrunksAsync(serverId, configProvider, "sip.conf", TrunkTechnology.Sip, 5060, trunks, range, ct);
        await LoadSimpleTrunksAsync(serverId, configProvider, "iax.conf", TrunkTechnology.Iax2, 4569, trunks, range, ct);

        await MergeStatusAsync(serverId, trunks, ct);

        return trunks;
    }

    private static async Task LoadPjsipTrunksAsync(
        string serverId,
        IConfigProvider configProvider,
        List<TrunkViewModel> trunks,
        (int Start, int End) range,
        CancellationToken ct)
    {
        var categories = await configProvider.GetCategoriesAsync(serverId, "pjsip.conf", ct);
        foreach (var endpoint in categories.Where(c =>
            c.Variables.GetValueOrDefault("type") == "endpoint"
            && !ExtensionService.IsInExtensionRange(c.Name, range.Start, range.End)))
        {
            trunks.Add(new TrunkViewModel
            {
                Name = endpoint.Name,
                Technology = TrunkTechnology.PjSip,
                Host = ExtractHostFromAor(categories, $"{endpoint.Name}-aor"),
                Port = ExtractPortFromAor(categories, $"{endpoint.Name}-aor"),
                Codecs = endpoint.Variables.GetValueOrDefault("allow", ""),
                Status = TrunkStatus.Unknown,
            });
        }
    }

    private static async Task LoadSimpleTrunksAsync(
        string serverId,
        IConfigProvider configProvider,
        string filename,
        TrunkTechnology technology,
        int defaultPort,
        List<TrunkViewModel> trunks,
        (int Start, int End) range,
        CancellationToken ct)
    {
        var categories = await configProvider.GetCategoriesAsync(serverId, filename, ct);
        foreach (var cat in categories)
        {
            if (cat.Variables.GetValueOrDefault("type") != "peer" || cat.Name == "general")
                continue;
            if (ExtensionService.IsInExtensionRange(cat.Name, range.Start, range.End))
                continue;

            trunks.Add(new TrunkViewModel
            {
                Name = cat.Name,
                Technology = technology,
                Host = cat.Variables.GetValueOrDefault("host", ""),
                Port = int.TryParse(cat.Variables.GetValueOrDefault("port"), out var p) ? p : defaultPort,
                Codecs = cat.Variables.GetValueOrDefault("allow", ""),
                Status = TrunkStatus.Unknown,
            });
        }
    }

    /// <summary>Gets detailed trunk information including config and live status.</summary>
    public async Task<TrunkDetailViewModel?> GetTrunkDetailAsync(string serverId, string name, TrunkTechnology technology, CancellationToken ct = default)
    {
        var config = await LoadTrunkConfigAsync(serverId, name, technology, ct);
        if (config is null) return null;

        var vm = new TrunkDetailViewModel
        {
            Name = config.Name,
            Technology = config.Technology,
            Host = config.Host,
            Port = config.Port,
            Codecs = config.Codecs,
            MaxChannels = config.MaxChannels,
            Config = config,
            Status = TrunkStatus.Unknown,
        };

        // Get detailed status
        if (technology == TrunkTechnology.PjSip)
        {
            var output = await _resolver.GetProvider(serverId).ExecuteCommandAsync(serverId, $"pjsip show endpoint {name}", ct);
            if (output is not null)
            {
                vm.ContactUri = ExtractField(output, "Contact:");
                vm.UserAgent = ExtractField(output, "UserAgent:");
                vm.RoundtripMs = ExtractRoundtrip(output);
                vm.Status = DetectPjsipStatus(output);
            }
        }

        return vm;
    }

    /// <summary>Creates a trunk with all required config sections.</summary>
    public async Task<bool> CreateTrunkAsync(string serverId, TrunkConfig config, CancellationToken ct = default)
    {
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(config.Technology);

        bool success;
        if (config.Technology == TrunkTechnology.PjSip)
        {
            success = await CreatePjsipSectionsAsync(configProvider, serverId, filename, config, ct);
        }
        else
        {
            var vars = config.Technology == TrunkTechnology.Sip ? config.ToSipPeer() : config.ToIaxPeer();
            success = await configProvider.CreateSectionAsync(serverId, filename, config.Name, vars, ct: ct);
        }

        if (!success) return false;

        // Reload the appropriate module
        if (!await configProvider.ReloadModuleAsync(serverId, GetReloadModule(config.Technology), ct))
            TrunkServiceLog.ReloadFailedCreate(_logger, config.Name);

        TrunkServiceLog.Created(_logger, serverId, config.Name, config.Technology);
        return true;
    }

    /// <summary>Updates a trunk by upserting each config section in place.</summary>
    public async Task<bool> UpdateTrunkAsync(string serverId, TrunkConfig config, CancellationToken ct = default)
    {
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(config.Technology);

        bool success;
        if (config.Technology == TrunkTechnology.PjSip)
        {
            success = await UpdatePjsipSectionsAsync(configProvider, serverId, filename, config, ct);
        }
        else
        {
            var vars = config.Technology == TrunkTechnology.Sip ? config.ToSipPeer() : config.ToIaxPeer();
            success = await configProvider.UpdateSectionAsync(serverId, filename, config.Name, vars, ct);
        }

        if (!success) return false;

        if (!await configProvider.ReloadModuleAsync(serverId, GetReloadModule(config.Technology), ct))
            TrunkServiceLog.ReloadFailedCreate(_logger, config.Name);

        return true;
    }

    private static async Task<bool> CreatePjsipSectionsAsync(
        IConfigProvider provider, string serverId, string filename, TrunkConfig config, CancellationToken ct)
    {
        if (!await provider.CreateSectionAsync(serverId, filename, config.Name, config.ToPjsipEndpoint(), ct: ct))
            return false;
        if (!await provider.CreateSectionAsync(serverId, filename, $"{config.Name}-auth", config.ToPjsipAuth(), ct: ct))
            return false;
        if (!await provider.CreateSectionAsync(serverId, filename, $"{config.Name}-aor", config.ToPjsipAor(), ct: ct))
            return false;

        if (config.ToPjsipRegistration() is { } regVars
            && !await provider.CreateSectionAsync(serverId, filename, $"{config.Name}-reg", regVars, ct: ct))
            return false;

        if (!string.IsNullOrEmpty(config.Host)
            && !await provider.CreateSectionAsync(serverId, filename, $"{config.Name}-identify", config.ToPjsipIdentify(), ct: ct))
            return false;

        return true;
    }

    private static async Task<bool> UpdatePjsipSectionsAsync(
        IConfigProvider provider, string serverId, string filename, TrunkConfig config, CancellationToken ct)
    {
        if (!await provider.UpdateSectionAsync(serverId, filename, config.Name, config.ToPjsipEndpoint(), ct))
            return false;
        if (!await provider.UpdateSectionAsync(serverId, filename, $"{config.Name}-auth", config.ToPjsipAuth(), ct))
            return false;
        if (!await provider.UpdateSectionAsync(serverId, filename, $"{config.Name}-aor", config.ToPjsipAor(), ct))
            return false;

        if (config.RegistrationEnabled && config.ToPjsipRegistration() is { } regVars
            && !await provider.UpdateSectionAsync(serverId, filename, $"{config.Name}-reg", regVars, ct))
            return false;

        if (!string.IsNullOrEmpty(config.Host)
            && !await provider.UpdateSectionAsync(serverId, filename, $"{config.Name}-identify", config.ToPjsipIdentify(), ct))
            return false;

        return true;
    }

    /// <summary>Deletes a trunk and all its config sections.</summary>
    public async Task<bool> DeleteTrunkAsync(string serverId, string name, TrunkTechnology technology, CancellationToken ct = default)
    {
        if (!await DeleteSectionsAsync(serverId, name, technology, ct))
            return false;

        if (!await _resolver.GetProvider(serverId).ReloadModuleAsync(serverId, GetReloadModule(technology), ct))
            TrunkServiceLog.ReloadFailedDelete(_logger, name);
        TrunkServiceLog.Deleted(_logger, serverId, name, technology);
        return true;
    }

    private async Task<bool> DeleteSectionsAsync(string serverId, string name, TrunkTechnology technology, CancellationToken ct)
    {
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(technology);

        if (technology == TrunkTechnology.PjSip)
        {
            // Delete all 5 possible PJSIP sections
            await configProvider.DeleteSectionAsync(serverId, filename, name, ct);
            await configProvider.DeleteSectionAsync(serverId, filename, $"{name}-auth", ct);
            await configProvider.DeleteSectionAsync(serverId, filename, $"{name}-aor", ct);
            await configProvider.DeleteSectionAsync(serverId, filename, $"{name}-reg", ct);
            await configProvider.DeleteSectionAsync(serverId, filename, $"{name}-identify", ct);
            return true;
        }

        return await configProvider.DeleteSectionAsync(serverId, filename, name, ct);
    }

    private async Task<TrunkConfig?> LoadTrunkConfigAsync(string serverId, string name, TrunkTechnology technology, CancellationToken ct)
    {
        var configProvider = _resolver.GetProvider(serverId);
        var filename = GetConfigFilename(technology);

        if (technology == TrunkTechnology.PjSip)
        {
            var categories = await configProvider.GetCategoriesAsync(serverId, filename, ct);
            var catDict = categories.ToDictionary(c => c.Name, c => c.Variables, StringComparer.OrdinalIgnoreCase);

            catDict.TryGetValue(name, out var endpoint);
            catDict.TryGetValue($"{name}-auth", out var auth);
            catDict.TryGetValue($"{name}-aor", out var aor);
            catDict.TryGetValue($"{name}-reg", out var reg);

            if (endpoint is null) return null;

            return TrunkConfig.FromPjsipSections(name, endpoint, auth, aor, reg);
        }

        var section = await configProvider.GetSectionAsync(serverId, filename, name, ct);
        if (section is null) return null;

        return technology == TrunkTechnology.Sip
            ? TrunkConfig.FromSipPeer(name, section)
            : TrunkConfig.FromIaxPeer(name, section);
    }

    private async Task MergeStatusAsync(string serverId, List<TrunkViewModel> trunks, CancellationToken ct)
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
                foreach (var trunk in trunks.Where(t => t.Technology == TrunkTechnology.PjSip))
                {
                    trunk.Status = DetectTrunkStatusFromOutput(pjsipOutput, trunk.Name);
                }
            }

            // Get SIP peer statuses
            var sipOutput = await configProvider.ExecuteCommandAsync(serverId, "sip show peers", cts.Token);
            if (sipOutput is not null)
            {
                foreach (var trunk in trunks.Where(t => t.Technology == TrunkTechnology.Sip))
                {
                    trunk.Status = DetectTrunkStatusFromOutput(sipOutput, trunk.Name);
                }
            }

            // Get IAX2 peer statuses
            var iaxOutput = await configProvider.ExecuteCommandAsync(serverId, "iax2 show peers", cts.Token);
            if (iaxOutput is not null)
            {
                foreach (var trunk in trunks.Where(t => t.Technology == TrunkTechnology.Iax2))
                {
                    trunk.Status = DetectTrunkStatusFromOutput(iaxOutput, trunk.Name);
                }
            }
        }
        catch (Exception ex)
        {
            TrunkServiceLog.MergeStatusFailed(_logger, ex, serverId);
        }
    }

    public static string GetConfigFilename(TrunkTechnology technology) => technology switch
    {
        TrunkTechnology.PjSip => "pjsip.conf",
        TrunkTechnology.Sip => "sip.conf",
        TrunkTechnology.Iax2 => "iax.conf",
        _ => "pjsip.conf"
    };

    public static string GetReloadModule(TrunkTechnology technology) => technology switch
    {
        TrunkTechnology.PjSip => "res_pjsip.so",
        TrunkTechnology.Sip => "chan_sip.so",
        TrunkTechnology.Iax2 => "chan_iax2.so",
        _ => "res_pjsip.so"
    };

    private static TrunkStatus DetectTrunkStatusFromOutput(string output, string trunkName)
    {
        // Find the line containing the trunk name and look for status keywords
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(trunkName, StringComparison.OrdinalIgnoreCase))
                continue;

            var upper = line.ToUpperInvariant();
            if (upper.Contains("AVAIL") || upper.Contains("REACHABLE") || upper.Contains("REGISTERED"))
                return TrunkStatus.Registered;
            if (upper.Contains("UNAVAIL") || upper.Contains("UNREACHABLE"))
                return TrunkStatus.Unreachable;
            if (upper.Contains("UNREGISTERED"))
                return TrunkStatus.Unregistered;
            if (upper.Contains("REJECTED"))
                return TrunkStatus.Rejected;
        }

        return TrunkStatus.Unknown;
    }

    private static TrunkStatus DetectPjsipStatus(string output)
    {
        var upper = output.ToUpperInvariant();
        if (upper.Contains("AVAIL") || upper.Contains("REACHABLE"))
            return TrunkStatus.Registered;
        if (upper.Contains("UNAVAIL") || upper.Contains("UNREACHABLE"))
            return TrunkStatus.Unreachable;
        return TrunkStatus.Unknown;
    }

    private static string? ExtractField(string output, string fieldName)
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

    private static int? ExtractRoundtrip(string output)
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

    private static string ExtractHostFromAor(List<ConfigCategory> categories, string aorName)
    {
        var aor = categories.FirstOrDefault(c =>
            string.Equals(c.Name, aorName, StringComparison.OrdinalIgnoreCase));
        if (aor is null) return "";

        var contact = aor.Variables.GetValueOrDefault("contact", "");
        if (!contact.StartsWith("sip:", StringComparison.Ordinal)) return contact;

        var hostPort = contact["sip:".Length..];
        var colonIdx = hostPort.LastIndexOf(':');
        return colonIdx > 0 ? hostPort[..colonIdx] : hostPort;
    }

    private static int ExtractPortFromAor(List<ConfigCategory> categories, string aorName)
    {
        var aor = categories.FirstOrDefault(c =>
            string.Equals(c.Name, aorName, StringComparison.OrdinalIgnoreCase));
        if (aor is null) return 5060;

        var contact = aor.Variables.GetValueOrDefault("contact", "");
        if (!contact.StartsWith("sip:", StringComparison.Ordinal)) return 5060;

        var hostPort = contact["sip:".Length..];
        var colonIdx = hostPort.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(hostPort[(colonIdx + 1)..], out var port))
            return port;
        return 5060;
    }
}
