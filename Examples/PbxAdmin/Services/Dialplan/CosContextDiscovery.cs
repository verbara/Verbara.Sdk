using System.Text.RegularExpressions;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services.Dialplan;

internal static partial class CosContextDiscoveryLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_DISCOVERY] Discovered contexts: server={ServerId} count={Count}")]
    public static partial void Discovered(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[COS_DISCOVERY] Mismatch: server={ServerId} extension={Extension} expected={Expected} actual={Actual}")]
    public static partial void Mismatch(ILogger logger, string serverId, string extension, string expected, string actual);

    [LoggerMessage(Level = LogLevel.Error, Message = "[COS_DISCOVERY] Discovery failed: server={ServerId}")]
    public static partial void DiscoveryFailed(ILogger logger, Exception exception, string serverId);
}

public sealed class CosContextDiscovery
{
    private static readonly Regex ContextPattern = new(
        @"\[ Context '([^']+)' created by '([^']+)' \]",
        RegexOptions.Compiled);

    private readonly IConfigProviderResolver _configResolver;
    private readonly ICosRepositoryResolver _cosResolver;
    private readonly ILogger<CosContextDiscovery> _logger;

    public CosContextDiscovery(
        IConfigProviderResolver configResolver,
        ICosRepositoryResolver cosResolver,
        ILogger<CosContextDiscovery> logger)
    {
        _configResolver = configResolver;
        _cosResolver = cosResolver;
        _logger = logger;
    }

    /// <summary>
    /// Discovers all Asterisk contexts by parsing AMI "dialplan show" output.
    /// </summary>
    public async Task<List<string>> DiscoverContextsAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var provider = _configResolver.GetProvider(serverId);
            var output = await provider.ExecuteCommandAsync(serverId, "dialplan show", ct);
            if (output is null)
                return [];

            var contexts = ParseContexts(output);
            CosContextDiscoveryLog.Discovered(_logger, serverId, contexts.Count);
            return contexts;
        }
        catch (Exception ex)
        {
            CosContextDiscoveryLog.DiscoveryFailed(_logger, ex, serverId);
            return [];
        }
    }

    /// <summary>
    /// Checks if a specific context exists in Asterisk's dialplan.
    /// </summary>
    public async Task<bool> ValidateContextExistsAsync(string serverId, string contextName, CancellationToken ct = default)
    {
        var contexts = await DiscoverContextsAsync(serverId, ct);
        return contexts.Contains(contextName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects extensions where endpoint context differs from COS-assigned context.
    /// </summary>
    public async Task<List<ContextMismatch>> DetectMismatchesAsync(string serverId, CancellationToken ct = default)
    {
        var mismatches = new List<ContextMismatch>();
        var cosRepo = _cosResolver.GetCosRepository(serverId);
        var overrides = await cosRepo.GetExtensionOverridesAsync(serverId, ct);

        // For each extension with a COS override, compare expected vs actual context.
        // Full implementation requires reading endpoint configs via PJSIP;
        // placeholder returns empty until endpoint context reading is wired.

        return mismatches;
    }

    /// <summary>
    /// Parses "dialplan show" output to extract context names.
    /// Lines matching: <c>[ Context 'name' created by 'module' ]</c>
    /// </summary>
    internal static List<string> ParseContexts(string output)
    {
        var contexts = new List<string>();

        foreach (var line in output.AsSpan().EnumerateLines())
        {
            var match = ContextPattern.Match(line.ToString());
            if (match.Success)
            {
                contexts.Add(match.Groups[1].Value);
            }
        }

        return contexts;
    }
}

public sealed record ContextMismatch(string Extension, string ExpectedContext, string ActualContext);
