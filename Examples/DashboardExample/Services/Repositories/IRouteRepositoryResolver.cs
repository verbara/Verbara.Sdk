namespace DashboardExample.Services.Repositories;

public interface IRouteRepositoryResolver
{
    IRouteRepository GetRepository(string serverId);
}
