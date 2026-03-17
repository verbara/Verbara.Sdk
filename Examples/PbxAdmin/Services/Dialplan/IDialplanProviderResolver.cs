namespace PbxAdmin.Services.Dialplan;

public interface IDialplanProviderResolver
{
    IDialplanProvider GetProvider(string serverId);
}
