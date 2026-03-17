using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

public interface IFeatureCodeRepository
{
    // Feature codes
    Task<List<FeatureCode>> GetAllFeatureCodesAsync(string serverId, CancellationToken ct = default);
    Task<FeatureCode?> GetFeatureCodeByIdAsync(int id, CancellationToken ct = default);
    Task<FeatureCode?> GetFeatureCodeByCodeAsync(string serverId, string code, CancellationToken ct = default);
    Task<int> InsertFeatureCodeAsync(FeatureCode featureCode, CancellationToken ct = default);
    Task UpdateFeatureCodeAsync(FeatureCode featureCode, CancellationToken ct = default);
    Task DeleteFeatureCodeAsync(int id, CancellationToken ct = default);

    // Parking lots
    Task<List<ParkingLotConfig>> GetAllParkingLotsAsync(string serverId, CancellationToken ct = default);
    Task<ParkingLotConfig?> GetParkingLotByIdAsync(int id, CancellationToken ct = default);
    Task<ParkingLotConfig?> GetParkingLotByNameAsync(string serverId, string name, CancellationToken ct = default);
    Task<int> InsertParkingLotAsync(ParkingLotConfig lot, CancellationToken ct = default);
    Task UpdateParkingLotAsync(ParkingLotConfig lot, CancellationToken ct = default);
    Task DeleteParkingLotAsync(int id, CancellationToken ct = default);
}
