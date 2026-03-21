using PbxAdmin.Models;

namespace PbxAdmin.Services;

/// <summary>
/// Service for managing extension configuration templates.
/// </summary>
public interface IExtensionTemplateService
{
    Task<IReadOnlyList<ExtensionTemplate>> GetAllAsync();
    Task<ExtensionConfig> ApplyTemplateAsync(int templateId);
    Task<int> SaveAsTemplateAsync(string name, string description, ExtensionConfig config);
    Task DeleteAsync(int id);
}
