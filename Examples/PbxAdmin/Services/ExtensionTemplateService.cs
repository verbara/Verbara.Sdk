using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

public sealed class ExtensionTemplateService(IExtensionTemplateRepository repo) : IExtensionTemplateService
{
    public Task<IReadOnlyList<ExtensionTemplate>> GetAllAsync() => repo.GetAllAsync();

    public async Task<ExtensionConfig> ApplyTemplateAsync(int templateId)
    {
        var template = await repo.GetByIdAsync(templateId);
        return template?.Config ?? new ExtensionConfig();
    }

    public Task<int> SaveAsTemplateAsync(string name, string description, ExtensionConfig config) =>
        repo.CreateAsync(new ExtensionTemplate
        {
            Name = name,
            Description = description,
            IsBuiltIn = false,
            Config = config
        });

    public Task DeleteAsync(int id) => repo.DeleteAsync(id);
}
