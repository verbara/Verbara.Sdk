namespace DashboardExample.Services.Dialplan;

public interface IDialplanProviderResolver
{
    IDialplanProvider GetProvider(string serverId);
}
