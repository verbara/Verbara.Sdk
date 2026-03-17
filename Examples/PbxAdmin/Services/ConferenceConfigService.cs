using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

public sealed partial class ConferenceConfigService
{
    private readonly IConferenceConfigRepository _repo;
    private readonly IRecordingMohSchemaManager _schema;
    private readonly IConfigProviderResolver _providerResolver;
    private readonly ILogger<ConferenceConfigService> _logger;

    public ConferenceConfigService(
        IConferenceConfigRepository repo,
        IRecordingMohSchemaManager schema,
        IConfigProviderResolver providerResolver,
        ILogger<ConferenceConfigService> logger)
    {
        _repo = repo;
        _schema = schema;
        _providerResolver = providerResolver;
        _logger = logger;
    }

    public async Task<List<ConferenceConfig>> GetConfigsAsync(string serverId, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetAllAsync(serverId, ct);
    }

    public async Task<ConferenceConfig?> GetConfigAsync(int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetByIdAsync(id, ct);
    }

    public async Task<(bool Success, string? Error)> CreateConfigAsync(
        string serverId, ConferenceConfig config, CancellationToken ct = default)
    {
        var error = ValidateConfig(config);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetByNameAsync(serverId, config.Name, ct);
        if (existing is not null) return (false, $"Conference '{config.Name}' already exists");

        config.ServerId = serverId;
        var id = await _repo.InsertAsync(config, ct);
        await RegenerateConfBridgeConfAsync(serverId, ct);
        ConfigCreated(_logger, id, config.Name);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateConfigAsync(
        string serverId, ConferenceConfig config, CancellationToken ct = default)
    {
        var error = ValidateConfig(config);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetByNameAsync(serverId, config.Name, ct);
        if (existing is not null && existing.Id != config.Id)
            return (false, $"Conference '{config.Name}' already exists");

        config.ServerId = serverId;
        await _repo.UpdateAsync(config, ct);
        await RegenerateConfBridgeConfAsync(serverId, ct);
        ConfigUpdated(_logger, config.Id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteConfigAsync(
        string serverId, int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        await _repo.DeleteAsync(id, ct);
        await RegenerateConfBridgeConfAsync(serverId, ct);
        ConfigDeleted(_logger, id);
        return (true, null);
    }

    // --- Config regeneration ---

    public async Task RegenerateConfBridgeConfAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var provider = _providerResolver.GetProvider(serverId);
            var configs = await _repo.GetAllAsync(serverId, ct);

            // Delete existing bridge profile sections (prefixed with "bridge_")
            var categories = await provider.GetCategoriesAsync(serverId, "confbridge.conf", ct);
            foreach (var cat in categories.Where(c => c.Name.StartsWith("bridge_", StringComparison.Ordinal)))
                await provider.DeleteSectionAsync(serverId, "confbridge.conf", cat.Name, ct);

            // Recreate from DB
            foreach (var cfg in configs)
            {
                var sectionName = $"bridge_{cfg.Name}";
                var lines = new List<KeyValuePair<string, string>>
                {
                    new("type", "bridge"),
                };

                if (cfg.MaxMembers > 0)
                    lines.Add(new("max_members", cfg.MaxMembers.ToString(CultureInfo.InvariantCulture)));
                if (cfg.Record)
                    lines.Add(new("record_conference", "yes"));
                if (!string.IsNullOrWhiteSpace(cfg.MusicOnHold) && cfg.MusicOnHold != "default")
                    lines.Add(new("music_on_hold_class", cfg.MusicOnHold));

                await provider.CreateSectionWithLinesAsync(serverId, "confbridge.conf", sectionName, lines, ct);
            }

            await provider.ExecuteCommandAsync(serverId, "module reload app_confbridge", ct);
            ConfRegenerated(_logger, serverId, configs.Count);
        }
        catch (Exception ex)
        {
            ConfRegenFailed(_logger, ex, serverId);
        }
    }

    // --- Static conf generator (for testing) ---

    public static string GenerateConfBridgeConf(List<ConferenceConfig> configs)
    {
        var sb = new StringBuilder();
        foreach (var cfg in configs)
        {
            sb.Append("[bridge_").Append(cfg.Name).AppendLine("]");
            sb.AppendLine("type=bridge");

            if (cfg.MaxMembers > 0)
                sb.Append("max_members=").AppendLine(cfg.MaxMembers.ToString(CultureInfo.InvariantCulture));
            if (cfg.Record)
                sb.AppendLine("record_conference=yes");
            if (!string.IsNullOrWhiteSpace(cfg.MusicOnHold) && cfg.MusicOnHold != "default")
                sb.Append("music_on_hold_class=").AppendLine(cfg.MusicOnHold);

            sb.AppendLine();
        }
        return sb.ToString();
    }

    // --- Validation ---

    private static string? ValidateConfig(ConferenceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return "Name is required";
        if (!ConfNameRegex().IsMatch(config.Name))
            return "Name must contain only letters, numbers, hyphens, and underscores";
        if (config.MaxMembers < 0)
            return "Max members cannot be negative";
        if (config.Pin is not null && config.Pin.Length > 0 && !PinRegex().IsMatch(config.Pin))
            return "PIN must contain only digits";
        if (config.AdminPin is not null && config.AdminPin.Length > 0 && !PinRegex().IsMatch(config.AdminPin))
            return "Admin PIN must contain only digits";
        return null;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex ConfNameRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex PinRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created conference config {Id}: {Name}")]
    private static partial void ConfigCreated(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated conference config {Id}")]
    private static partial void ConfigUpdated(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted conference config {Id}")]
    private static partial void ConfigDeleted(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Regenerated confbridge.conf for {ServerId}: {Count} bridges")]
    private static partial void ConfRegenerated(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to regenerate confbridge.conf for {ServerId}")]
    private static partial void ConfRegenFailed(ILogger logger, Exception ex, string serverId);
}
