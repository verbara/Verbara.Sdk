using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using DashboardExample.Models;

namespace DashboardExample.Services;

internal static partial class TrunkServiceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to merge trunk status for {ServerId}")]
    public static partial void MergeStatusFailed(ILogger logger, Exception exception, string serverId);
}

/// <summary>
/// CRUD service for Asterisk trunks (PJSIP, SIP, IAX2).
/// </summary>
public sealed class TrunkService
{
    private readonly IConfigProvider _configProvider;
    private readonly AsteriskMonitorService _monitor;
    private readonly ILogger<TrunkService> _logger;

    public TrunkService(IConfigProvider configProvider, AsteriskMonitorService monitor, ILogger<TrunkService> logger)
    {
        _configProvider = configProvider;
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>Gets all trunks from all config files, merged with live status.</summary>
    public async Task<List<TrunkViewModel>> GetTrunksAsync(string serverId, CancellationToken ct = default)
    {
        var trunks = new List<TrunkViewModel>();

        // Load PJSIP trunks
        var pjsipCategories = await _configProvider.GetCategoriesAsync(serverId, "pjsip.conf", ct);
        var pjsipEndpoints = pjsipCategories
            .Where(c => c.Variables.GetValueOrDefault("type") == "endpoint")
            .ToList();

        foreach (var endpoint in pjsipEndpoints)
        {
            trunks.Add(new TrunkViewModel
            {
                Name = endpoint.Name,
                Technology = TrunkTechnology.PjSip,
                Host = ExtractHostFromAor(pjsipCategories, $"{endpoint.Name}-aor"),
                Port = ExtractPortFromAor(pjsipCategories, $"{endpoint.Name}-aor"),
                Codecs = endpoint.Variables.GetValueOrDefault("allow", ""),
                Status = TrunkStatus.Unknown,
            });
        }

        // Load SIP trunks
        var sipCategories = await _configProvider.GetCategoriesAsync(serverId, "sip.conf", ct);
        foreach (var cat in sipCategories)
        {
            if (cat.Variables.GetValueOrDefault("type") != "peer" || cat.Name == "general")
                continue;

            trunks.Add(new TrunkViewModel
            {
                Name = cat.Name,
                Technology = TrunkTechnology.Sip,
                Host = cat.Variables.GetValueOrDefault("host", ""),
                Port = int.TryParse(cat.Variables.GetValueOrDefault("port"), out var p) ? p : 5060,
                Codecs = cat.Variables.GetValueOrDefault("allow", ""),
                Status = TrunkStatus.Unknown,
            });
        }

        // Load IAX2 trunks
        var iaxCategories = await _configProvider.GetCategoriesAsync(serverId, "iax.conf", ct);
        foreach (var cat in iaxCategories)
        {
            if (cat.Variables.GetValueOrDefault("type") != "peer" || cat.Name == "general")
                continue;

            trunks.Add(new TrunkViewModel
            {
                Name = cat.Name,
                Technology = TrunkTechnology.Iax2,
                Host = cat.Variables.GetValueOrDefault("host", ""),
                Port = int.TryParse(cat.Variables.GetValueOrDefault("port"), out var p) ? p : 4569,
                Codecs = cat.Variables.GetValueOrDefault("allow", ""),
                Status = TrunkStatus.Unknown,
            });
        }

        // Merge with live status via AMI commands
        await MergeStatusAsync(serverId, trunks, ct);

        return trunks;
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
            var output = await _configProvider.ExecuteCommandAsync(serverId, $"pjsip show endpoint {name}", ct);
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
        var filename = GetConfigFilename(config.Technology);
        bool success;

        if (config.Technology == TrunkTechnology.PjSip)
        {
            // Create 4 PJSIP sections: endpoint, auth, aor, registration
            success = await _configProvider.CreateSectionAsync(serverId, filename, config.Name, config.ToPjsipEndpoint(), ct: ct);
            if (!success) return false;

            success = await _configProvider.CreateSectionAsync(serverId, filename, $"{config.Name}-auth", config.ToPjsipAuth(), ct: ct);
            if (!success) return false;

            success = await _configProvider.CreateSectionAsync(serverId, filename, $"{config.Name}-aor", config.ToPjsipAor(), ct: ct);
            if (!success) return false;

            var regVars = config.ToPjsipRegistration();
            if (regVars is not null)
            {
                success = await _configProvider.CreateSectionAsync(serverId, filename, $"{config.Name}-reg", regVars, ct: ct);
                if (!success) return false;
            }
        }
        else
        {
            var vars = config.Technology == TrunkTechnology.Sip ? config.ToSipPeer() : config.ToIaxPeer();
            success = await _configProvider.CreateSectionAsync(serverId, filename, config.Name, vars, ct: ct);
            if (!success) return false;
        }

        // Reload the appropriate module
        await _configProvider.ReloadModuleAsync(serverId, GetReloadModule(config.Technology), ct);

        return true;
    }

    /// <summary>Updates a trunk by deleting and recreating all sections.</summary>
    public async Task<bool> UpdateTrunkAsync(string serverId, TrunkConfig config, CancellationToken ct = default)
    {
        // Delete existing sections first
        if (!await DeleteSectionsAsync(serverId, config.Name, config.Technology, ct))
            return false;

        // Recreate with new config
        return await CreateTrunkAsync(serverId, config, ct);
    }

    /// <summary>Deletes a trunk and all its config sections.</summary>
    public async Task<bool> DeleteTrunkAsync(string serverId, string name, TrunkTechnology technology, CancellationToken ct = default)
    {
        if (!await DeleteSectionsAsync(serverId, name, technology, ct))
            return false;

        await _configProvider.ReloadModuleAsync(serverId, GetReloadModule(technology), ct);
        return true;
    }

    private async Task<bool> DeleteSectionsAsync(string serverId, string name, TrunkTechnology technology, CancellationToken ct)
    {
        var filename = GetConfigFilename(technology);

        if (technology == TrunkTechnology.PjSip)
        {
            // Delete all 4 possible PJSIP sections
            await _configProvider.DeleteSectionAsync(serverId, filename, name, ct);
            await _configProvider.DeleteSectionAsync(serverId, filename, $"{name}-auth", ct);
            await _configProvider.DeleteSectionAsync(serverId, filename, $"{name}-aor", ct);
            await _configProvider.DeleteSectionAsync(serverId, filename, $"{name}-reg", ct);
            return true;
        }

        return await _configProvider.DeleteSectionAsync(serverId, filename, name, ct);
    }

    private async Task<TrunkConfig?> LoadTrunkConfigAsync(string serverId, string name, TrunkTechnology technology, CancellationToken ct)
    {
        var filename = GetConfigFilename(technology);

        if (technology == TrunkTechnology.PjSip)
        {
            var categories = await _configProvider.GetCategoriesAsync(serverId, filename, ct);
            var catDict = categories.ToDictionary(c => c.Name, c => c.Variables, StringComparer.OrdinalIgnoreCase);

            catDict.TryGetValue(name, out var endpoint);
            catDict.TryGetValue($"{name}-auth", out var auth);
            catDict.TryGetValue($"{name}-aor", out var aor);
            catDict.TryGetValue($"{name}-reg", out var reg);

            if (endpoint is null) return null;

            return TrunkConfig.FromPjsipSections(name, endpoint, auth, aor, reg);
        }

        var section = await _configProvider.GetSectionAsync(serverId, filename, name, ct);
        if (section is null) return null;

        return technology == TrunkTechnology.Sip
            ? TrunkConfig.FromSipPeer(name, section)
            : TrunkConfig.FromIaxPeer(name, section);
    }

    private async Task MergeStatusAsync(string serverId, List<TrunkViewModel> trunks, CancellationToken ct)
    {
        try
        {
            // Get PJSIP endpoint statuses
            var pjsipOutput = await _configProvider.ExecuteCommandAsync(serverId, "pjsip show endpoints", ct);
            if (pjsipOutput is not null)
            {
                foreach (var trunk in trunks.Where(t => t.Technology == TrunkTechnology.PjSip))
                {
                    trunk.Status = DetectTrunkStatusFromOutput(pjsipOutput, trunk.Name);
                }
            }

            // Get SIP peer statuses
            var sipOutput = await _configProvider.ExecuteCommandAsync(serverId, "sip show peers", ct);
            if (sipOutput is not null)
            {
                foreach (var trunk in trunks.Where(t => t.Technology == TrunkTechnology.Sip))
                {
                    trunk.Status = DetectTrunkStatusFromOutput(sipOutput, trunk.Name);
                }
            }

            // Get IAX2 peer statuses
            var iaxOutput = await _configProvider.ExecuteCommandAsync(serverId, "iax2 show peers", ct);
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
