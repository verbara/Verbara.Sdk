using PbxAdmin.Models;

namespace PbxAdmin.Services.Repositories;

public interface IExtensionTemplateRepository
{
    Task<IReadOnlyList<ExtensionTemplate>> GetAllAsync();
    Task<ExtensionTemplate?> GetByIdAsync(int id);
    Task<int> CreateAsync(ExtensionTemplate extensionTemplate);
    Task DeleteAsync(int id);
}
