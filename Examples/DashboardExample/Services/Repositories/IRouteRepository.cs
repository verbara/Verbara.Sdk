using DashboardExample.Models;

namespace DashboardExample.Services.Repositories;

public interface IRouteRepository
{
    Task<List<InboundRouteConfig>> GetInboundRoutesAsync(string serverId, CancellationToken ct = default);
    Task<InboundRouteConfig?> GetInboundRouteAsync(int id, CancellationToken ct = default);
    Task<int> CreateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default);
    Task<bool> UpdateInboundRouteAsync(InboundRouteConfig config, CancellationToken ct = default);
    Task<bool> DeleteInboundRouteAsync(int id, CancellationToken ct = default);

    Task<List<OutboundRouteConfig>> GetOutboundRoutesAsync(string serverId, CancellationToken ct = default);
    Task<OutboundRouteConfig?> GetOutboundRouteAsync(int id, CancellationToken ct = default);
    Task<int> CreateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default);
    Task<bool> UpdateOutboundRouteAsync(OutboundRouteConfig config, CancellationToken ct = default);
    Task<bool> DeleteOutboundRouteAsync(int id, CancellationToken ct = default);

    Task<List<TimeConditionConfig>> GetTimeConditionsAsync(string serverId, CancellationToken ct = default);
    Task<TimeConditionConfig?> GetTimeConditionAsync(int id, CancellationToken ct = default);
    Task<int> CreateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default);
    Task<bool> UpdateTimeConditionAsync(TimeConditionConfig config, CancellationToken ct = default);
    Task<bool> DeleteTimeConditionAsync(int id, CancellationToken ct = default);

    Task<bool> IsTimeConditionReferencedAsync(int timeConditionId, CancellationToken ct = default);
}
