using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services.Dialplan;

public sealed class DialplanRegenerator(
    IRouteRepositoryResolver repoResolver,
    IDialplanProviderResolver dialplanResolver,
    IIvrMenuRepository ivrRepo,
    CosDialplanGenerator? cosGenerator = null)
{
    public async Task<(bool Success, string? Error)> RegenerateAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var repo = repoResolver.GetRepository(serverId);

            // Load COS contexts if generator is available
            List<DialplanLine>? cosContexts = null;
            if (cosGenerator is not null)
                cosContexts = await cosGenerator.GenerateContextsAsync(serverId, ct);

            var data = new DialplanData(
                await repo.GetInboundRoutesAsync(serverId, ct),
                await repo.GetOutboundRoutesAsync(serverId, ct),
                await repo.GetTimeConditionsAsync(serverId, ct),
                await ivrRepo.GetMenusAsync(serverId, ct),
                cosContexts);

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
