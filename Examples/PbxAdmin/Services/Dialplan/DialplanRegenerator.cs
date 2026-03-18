using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services.Dialplan;

public sealed class DialplanRegenerator(
    IRouteRepositoryResolver repoResolver,
    IDialplanProviderResolver dialplanResolver,
    IIvrMenuRepository ivrRepo)
{
    public async Task<(bool Success, string? Error)> RegenerateAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var repo = repoResolver.GetRepository(serverId);
            var data = new DialplanData(
                await repo.GetInboundRoutesAsync(serverId, ct),
                await repo.GetOutboundRoutesAsync(serverId, ct),
                await repo.GetTimeConditionsAsync(serverId, ct),
                await ivrRepo.GetMenusAsync(serverId, ct));

            var provider = dialplanResolver.GetProvider(serverId);
            await provider.GenerateDialplanAsync(serverId, data, ct);
            await provider.ReloadAsync(serverId, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Dialplan regeneration failed: {ex.Message}");
        }
    }
}
