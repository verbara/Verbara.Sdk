using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

public sealed partial class FeatureCodeService
{
    private readonly IFeatureCodeRepository _repo;
    private readonly IRecordingMohSchemaManager _schema;
    private readonly IConfigProviderResolver _providerResolver;
    private readonly ILogger<FeatureCodeService> _logger;

    public FeatureCodeService(
        IFeatureCodeRepository repo,
        IRecordingMohSchemaManager schema,
        IConfigProviderResolver providerResolver,
        ILogger<FeatureCodeService> logger)
    {
        _repo = repo;
        _schema = schema;
        _providerResolver = providerResolver;
        _logger = logger;
    }

    // --- Feature Codes ---

    public async Task<List<FeatureCode>> GetFeatureCodesAsync(string serverId, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetAllFeatureCodesAsync(serverId, ct);
    }

    public async Task<FeatureCode?> GetFeatureCodeAsync(int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetFeatureCodeByIdAsync(id, ct);
    }

    public async Task<(bool Success, string? Error)> CreateFeatureCodeAsync(
        string serverId, FeatureCode code, CancellationToken ct = default)
    {
        var error = ValidateFeatureCode(code);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetFeatureCodeByCodeAsync(serverId, code.Code, ct);
        if (existing is not null) return (false, $"Feature code '{code.Code}' already exists");

        code.ServerId = serverId;
        var id = await _repo.InsertFeatureCodeAsync(code, ct);
        await RegenerateFeaturesConfAsync(serverId, ct);
        FeatureCodeCreated(_logger, id, code.Code);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateFeatureCodeAsync(
        string serverId, FeatureCode code, CancellationToken ct = default)
    {
        var error = ValidateFeatureCode(code);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetFeatureCodeByCodeAsync(serverId, code.Code, ct);
        if (existing is not null && existing.Id != code.Id)
            return (false, $"Feature code '{code.Code}' already exists");

        code.ServerId = serverId;
        await _repo.UpdateFeatureCodeAsync(code, ct);
        await RegenerateFeaturesConfAsync(serverId, ct);
        FeatureCodeUpdated(_logger, code.Id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteFeatureCodeAsync(
        string serverId, int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        await _repo.DeleteFeatureCodeAsync(id, ct);
        await RegenerateFeaturesConfAsync(serverId, ct);
        FeatureCodeDeleted(_logger, id);
        return (true, null);
    }

    // --- Parking Lots ---

    public async Task<List<ParkingLotConfig>> GetParkingLotsAsync(string serverId, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetAllParkingLotsAsync(serverId, ct);
    }

    public async Task<ParkingLotConfig?> GetParkingLotAsync(int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetParkingLotByIdAsync(id, ct);
    }

    public async Task<(bool Success, string? Error)> CreateParkingLotAsync(
        string serverId, ParkingLotConfig lot, CancellationToken ct = default)
    {
        var error = ValidateParkingLot(lot);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetParkingLotByNameAsync(serverId, lot.Name, ct);
        if (existing is not null) return (false, $"Parking lot '{lot.Name}' already exists");

        lot.ServerId = serverId;
        var id = await _repo.InsertParkingLotAsync(lot, ct);
        await RegenerateFeaturesConfAsync(serverId, ct);
        ParkingLotCreated(_logger, id, lot.Name);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateParkingLotAsync(
        string serverId, ParkingLotConfig lot, CancellationToken ct = default)
    {
        var error = ValidateParkingLot(lot);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetParkingLotByNameAsync(serverId, lot.Name, ct);
        if (existing is not null && existing.Id != lot.Id)
            return (false, $"Parking lot '{lot.Name}' already exists");

        lot.ServerId = serverId;
        await _repo.UpdateParkingLotAsync(lot, ct);
        await RegenerateFeaturesConfAsync(serverId, ct);
        ParkingLotUpdated(_logger, lot.Id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteParkingLotAsync(
        string serverId, int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        await _repo.DeleteParkingLotAsync(id, ct);
        await RegenerateFeaturesConfAsync(serverId, ct);
        ParkingLotDeleted(_logger, id);
        return (true, null);
    }

    // --- Config regeneration ---

    public async Task RegenerateFeaturesConfAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var provider = _providerResolver.GetProvider(serverId);

            // Regenerate res_parking.conf for parking lots
            var lots = await _repo.GetAllParkingLotsAsync(serverId, ct);
            var parkingCategories = await provider.GetCategoriesAsync(serverId, "res_parking.conf", ct);
            foreach (var cat in parkingCategories)
                await provider.DeleteSectionAsync(serverId, "res_parking.conf", cat.Name, ct);

            foreach (var lot in lots)
            {
                var lines = new List<KeyValuePair<string, string>>
                {
                    new("parkext", lot.Name == "default" ? "700" : ""),
                    new("parkpos", $"{lot.ParkingStartSlot.ToString(CultureInfo.InvariantCulture)}-{lot.ParkingEndSlot.ToString(CultureInfo.InvariantCulture)}"),
                    new("context", lot.Context),
                    new("parkingtime", lot.ParkingTimeout.ToString(CultureInfo.InvariantCulture)),
                };

                if (lot.MusicOnHold != "default")
                    lines.Add(new("parkedmusicclass", lot.MusicOnHold));

                await provider.CreateSectionWithLinesAsync(serverId, "res_parking.conf", lot.Name, lines, ct);
            }

            await provider.ExecuteCommandAsync(serverId, "module reload res_parking", ct);

            // Reload features for feature codes
            await provider.ExecuteCommandAsync(serverId, "module reload features", ct);
            await provider.ExecuteCommandAsync(serverId, "dialplan reload", ct);

            FeaturesRegenerated(_logger, serverId, lots.Count);
        }
        catch (Exception ex)
        {
            FeaturesRegenFailed(_logger, ex, serverId);
        }
    }

    // --- Static conf generator (for testing) ---

    public static string GenerateResParking(List<ParkingLotConfig> lots)
    {
        var sb = new StringBuilder();
        foreach (var lot in lots)
        {
            sb.Append('[').Append(lot.Name).AppendLine("]");
            if (lot.Name == "default")
                sb.AppendLine("parkext=700");
            sb.Append("parkpos=")
              .Append(lot.ParkingStartSlot.ToString(CultureInfo.InvariantCulture))
              .Append('-')
              .AppendLine(lot.ParkingEndSlot.ToString(CultureInfo.InvariantCulture));
            sb.Append("context=").AppendLine(lot.Context);
            sb.Append("parkingtime=").AppendLine(lot.ParkingTimeout.ToString(CultureInfo.InvariantCulture));

            if (lot.MusicOnHold != "default")
                sb.Append("parkedmusicclass=").AppendLine(lot.MusicOnHold);

            sb.AppendLine();
        }
        return sb.ToString();
    }

    // --- Validation ---

    private static string? ValidateFeatureCode(FeatureCode code)
    {
        if (string.IsNullOrWhiteSpace(code.Code))
            return "Code is required";
        if (!StarCodeRegex().IsMatch(code.Code))
            return "Code must start with * or # followed by digits (e.g. *72, #21)";
        if (string.IsNullOrWhiteSpace(code.Name))
            return "Name is required";
        return null;
    }

    private static string? ValidateParkingLot(ParkingLotConfig lot)
    {
        if (string.IsNullOrWhiteSpace(lot.Name))
            return "Name is required";
        if (!LotNameRegex().IsMatch(lot.Name))
            return "Name must contain only letters, numbers, hyphens, and underscores";
        if (lot.ParkingStartSlot < 1 || lot.ParkingEndSlot < 1)
            return "Parking slots must be positive";
        if (lot.ParkingStartSlot >= lot.ParkingEndSlot)
            return "Start slot must be less than end slot";
        if (lot.ParkingTimeout < 0)
            return "Timeout cannot be negative";
        return null;
    }

    [GeneratedRegex(@"^[*#]\d{1,4}$")]
    private static partial Regex StarCodeRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex LotNameRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created feature code {Id}: {Code}")]
    private static partial void FeatureCodeCreated(ILogger logger, int id, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated feature code {Id}")]
    private static partial void FeatureCodeUpdated(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted feature code {Id}")]
    private static partial void FeatureCodeDeleted(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created parking lot {Id}: {Name}")]
    private static partial void ParkingLotCreated(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated parking lot {Id}")]
    private static partial void ParkingLotUpdated(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted parking lot {Id}")]
    private static partial void ParkingLotDeleted(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Regenerated features/parking for {ServerId}: {Count} parking lots")]
    private static partial void FeaturesRegenerated(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to regenerate features for {ServerId}")]
    private static partial void FeaturesRegenFailed(ILogger logger, Exception ex, string serverId);
}
