namespace PbxAdmin.Services.Repositories;

public interface ICosRepositoryResolver
{
    ICosRepository GetCosRepository(string serverId);
    ICosPatternGroupRepository GetPatternGroupRepository(string serverId);
}
