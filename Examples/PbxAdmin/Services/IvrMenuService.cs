using System.Text.RegularExpressions;
using PbxAdmin.Models;
using PbxAdmin.Services.Dialplan;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

internal static partial class IvrMenuServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[IVR-CFG] Created: server={ServerId} menu={MenuName}")]
    public static partial void Created(ILogger logger, string serverId, string menuName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[IVR-CFG] Updated: server={ServerId} menu={MenuName}")]
    public static partial void Updated(ILogger logger, string serverId, string menuName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[IVR-CFG] Deleted: server={ServerId} menuId={MenuId}")]
    public static partial void Deleted(ILogger logger, string serverId, int menuId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[IVR-CFG] Depth warning: menu={MenuName} depth={Depth}")]
    public static partial void DepthWarning(ILogger logger, string menuName, int depth);

    [LoggerMessage(Level = LogLevel.Error, Message = "[IVR-CFG] Operation failed: server={ServerId}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string serverId);
}

public sealed partial class IvrMenuService
{
    private const int MaxDepth = 5;
    private const int WarnDepth = 3;

    private static readonly string[] ValidDestTypes = ["extension", "queue", "ivr", "voicemail", "hangup", "external"];
    private static readonly string[] ValidDigits = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "#"];

    [GeneratedRegex(@"^[a-zA-Z0-9-]+$")]
    private static partial Regex ValidNameRegex();

    private readonly IIvrMenuRepository _repo;
    private readonly DialplanRegenerator _regenerator;
    private readonly ILogger<IvrMenuService> _logger;

    public IvrMenuService(
        IIvrMenuRepository repo,
        DialplanRegenerator regenerator,
        ILogger<IvrMenuService> logger)
    {
        _repo = repo;
        _regenerator = regenerator;
        _logger = logger;
    }

    // ─── Queries ───

    public async Task<List<IvrMenuViewModel>> GetRootMenusAsync(string serverId, CancellationToken ct = default)
    {
        var allMenus = await _repo.GetMenusAsync(serverId, ct);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var menu in allMenus)
            foreach (var item in menu.Items.Where(i => i.DestType == "ivr"))
                referenced.Add(item.DestTarget);

        var byName = allMenus.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        var result = new List<IvrMenuViewModel>();
        foreach (var menu in allMenus.Where(m => !referenced.Contains(m.Name)))
        {
            var (depth, subCount) = ComputeTreeStats(menu.Name, byName, []);
            result.Add(new IvrMenuViewModel
            {
                Id = menu.Id,
                Name = menu.Name,
                Label = menu.Label,
                Greeting = menu.Greeting,
                ItemCount = menu.Items.Count,
                MaxDepth = depth,
                SubMenuCount = subCount,
                Enabled = menu.Enabled,
                IsReferenced = false
            });
        }

        return result;
    }

    public Task<IvrMenuConfig?> GetMenuAsync(int id, CancellationToken ct = default)
        => _repo.GetMenuAsync(id, ct);

    public async Task<List<string>> GetAllMenuNamesAsync(string serverId, CancellationToken ct = default)
    {
        var menus = await _repo.GetMenusAsync(serverId, ct);
        return menus.Select(m => m.Name).ToList();
    }

    public async Task<IvrMenuTreeNode?> GetTreeAsync(int menuId, CancellationToken ct = default)
    {
        var menu = await _repo.GetMenuAsync(menuId, ct);
        if (menu is null) return null;

        var allMenus = await _repo.GetMenusAsync(menu.ServerId, ct);
        var byName = allMenus.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        return BuildTreeNode(menu, null, byName, []);
    }

    public async Task<List<IvrMenuConfig>> GetChildrenAsync(int menuId, CancellationToken ct = default)
    {
        var menu = await _repo.GetMenuAsync(menuId, ct);
        if (menu is null) return [];

        var allMenus = await _repo.GetMenusAsync(menu.ServerId, ct);
        var byName = allMenus.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        return menu.Items
            .Where(i => i.DestType == "ivr" && byName.ContainsKey(i.DestTarget))
            .Select(i => byName[i.DestTarget])
            .ToList();
    }

    // ─── CRUD ───

    public async Task<(bool Success, string? Error)> CreateMenuAsync(string serverId, IvrMenuConfig config, CancellationToken ct = default)
    {
        config.ServerId = serverId;
        var error = ValidateMenu(config);
        if (error is not null) return (false, error);

        var existing = await _repo.GetMenuByNameAsync(serverId, config.Name, ct);
        if (existing is not null)
            return (false, $"IVR menu '{config.Name}' already exists on this server");

        var allMenus = await _repo.GetMenusAsync(serverId, ct);
        var byName = allMenus.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        byName[config.Name] = config;

        foreach (var item in config.Items.Where(i => i.DestType == "ivr"))
        {
            if (!byName.ContainsKey(item.DestTarget))
                return (false, $"Referenced IVR menu '{item.DestTarget}' does not exist");
        }

        if (HasCycle(config.Name, byName))
            return (false, "Circular IVR reference detected");

        var depth = ComputeTreeStats(config.Name, byName, []).Depth;
        if (depth > MaxDepth)
            return (false, $"IVR nesting depth ({depth}) exceeds maximum of {MaxDepth}");
        if (depth > WarnDepth)
            IvrMenuServiceLog.DepthWarning(_logger, config.Name, depth);

        try
        {
            await _repo.CreateMenuAsync(config, ct);
            await _regenerator.RegenerateAsync(serverId, ct);
            IvrMenuServiceLog.Created(_logger, serverId, config.Name);
            return (true, null);
        }
        catch (Exception ex)
        {
            IvrMenuServiceLog.OperationFailed(_logger, ex, serverId);
            return (false, "Failed to create IVR menu");
        }
    }

    public async Task<(bool Success, string? Error)> UpdateMenuAsync(IvrMenuConfig config, CancellationToken ct = default)
    {
        var error = ValidateMenu(config);
        if (error is not null) return (false, error);

        var allMenus = await _repo.GetMenusAsync(config.ServerId, ct);
        var byName = allMenus.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        byName[config.Name] = config;

        foreach (var item in config.Items.Where(i => i.DestType == "ivr"))
        {
            if (!byName.ContainsKey(item.DestTarget))
                return (false, $"Referenced IVR menu '{item.DestTarget}' does not exist");
        }

        if (HasCycle(config.Name, byName))
            return (false, "Circular IVR reference detected");

        var depth = ComputeTreeStats(config.Name, byName, []).Depth;
        if (depth > MaxDepth)
            return (false, $"IVR nesting depth ({depth}) exceeds maximum of {MaxDepth}");
        if (depth > WarnDepth)
            IvrMenuServiceLog.DepthWarning(_logger, config.Name, depth);

        try
        {
            await _repo.UpdateMenuAsync(config, ct);
            await _regenerator.RegenerateAsync(config.ServerId, ct);
            IvrMenuServiceLog.Updated(_logger, config.ServerId, config.Name);
            return (true, null);
        }
        catch (Exception ex)
        {
            IvrMenuServiceLog.OperationFailed(_logger, ex, config.ServerId);
            return (false, "Failed to update IVR menu");
        }
    }

    public async Task<(bool Success, string? Error)> DeleteMenuAsync(string serverId, int id, CancellationToken ct = default)
    {
        var isRef = await _repo.IsMenuReferencedAsync(id, ct);
        if (isRef)
            return (false, "Cannot delete: this IVR menu is referenced by other menus, routes, or time conditions");

        try
        {
            await _repo.DeleteMenuAsync(id, ct);
            await _regenerator.RegenerateAsync(serverId, ct);
            IvrMenuServiceLog.Deleted(_logger, serverId, id);
            return (true, null);
        }
        catch (Exception ex)
        {
            IvrMenuServiceLog.OperationFailed(_logger, ex, serverId);
            return (false, "Failed to delete IVR menu");
        }
    }

    // ─── Audio validation ───

    public Task<(bool Exists, string? Warning)> ValidateGreetingAsync(string serverId, string greeting, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(greeting))
            return Task.FromResult<(bool, string?)>((true, null));

        return Task.FromResult<(bool, string?)>((true, "Audio validation requires AMI connection (not yet wired)"));
    }

    // ─── Validation ───

    internal static string? ValidateMenu(IvrMenuConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return "Menu name is required";
        if (!ValidNameRegex().IsMatch(config.Name))
            return "Menu name must contain only letters, numbers, and hyphens";
        if (string.IsNullOrWhiteSpace(config.Label))
            return "Menu label is required";

        var seenDigits = new HashSet<string>();
        foreach (var item in config.Items)
        {
            if (!ValidDigits.Contains(item.Digit))
                return $"Invalid digit '{item.Digit}': must be 0-9, *, or #";
            if (!seenDigits.Add(item.Digit))
                return $"Duplicate digit '{item.Digit}'";
            if (!ValidDestTypes.Contains(item.DestType))
                return $"Invalid destination type '{item.DestType}'";
            if (item.DestType != "hangup" && string.IsNullOrWhiteSpace(item.DestTarget))
                return $"Destination target is required for digit '{item.Digit}'";
            if (item.DestType == "external" && string.IsNullOrWhiteSpace(item.Trunk))
                return $"Trunk is required for external destination on digit '{item.Digit}' (or configure an outbound trunk)";
        }

        return null;
    }

    // ─── Cycle detection ───

    internal static bool HasCycle(string menuName, Dictionary<string, IvrMenuConfig> allMenus, HashSet<string>? path = null)
    {
        path ??= new(StringComparer.OrdinalIgnoreCase);
        if (!path.Add(menuName)) return true;

        if (allMenus.TryGetValue(menuName, out var menu))
        {
            foreach (var item in menu.Items.Where(i => i.DestType == "ivr"))
            {
                if (HasCycle(item.DestTarget, allMenus, path))
                    return true;
            }
        }

        path.Remove(menuName);
        return false;
    }

    // ─── Tree helpers ───

    private static (int Depth, int SubCount) ComputeTreeStats(
        string menuName, Dictionary<string, IvrMenuConfig> byName, HashSet<string> visited)
    {
        if (!visited.Add(menuName) || !byName.TryGetValue(menuName, out var menu))
            return (1, 0);

        var maxChildDepth = 0;
        var subCount = 0;

        foreach (var item in menu.Items.Where(i => i.DestType == "ivr"))
        {
            subCount++;
            var (childDepth, childSubs) = ComputeTreeStats(item.DestTarget, byName, visited);
            maxChildDepth = Math.Max(maxChildDepth, childDepth);
            subCount += childSubs;
        }

        visited.Remove(menuName);
        return (1 + maxChildDepth, subCount);
    }

    private static IvrMenuTreeNode BuildTreeNode(
        IvrMenuConfig menu, string? digit,
        Dictionary<string, IvrMenuConfig> byName, HashSet<int> visited)
    {
        var node = new IvrMenuTreeNode
        {
            MenuId = menu.Id,
            Name = menu.Name,
            Label = menu.Label,
            Digit = digit
        };

        if (!visited.Add(menu.Id)) return node;

        foreach (var item in menu.Items)
        {
            if (item.DestType == "ivr" && byName.TryGetValue(item.DestTarget, out var subMenu))
            {
                node.Children.Add(BuildTreeNode(subMenu, item.Digit, byName, visited));
            }
            else
            {
                node.Children.Add(new IvrMenuTreeNode
                {
                    MenuId = 0,
                    Name = item.DestTarget,
                    Label = FormatLeafLabel(item),
                    Digit = item.Digit
                });
            }
        }

        visited.Remove(menu.Id);
        return node;
    }

    private static string FormatLeafLabel(IvrMenuItemConfig item) => item.DestType switch
    {
        "extension" => $"Ext {item.DestTarget}",
        "queue" => $"Queue: {item.DestTarget}",
        "voicemail" => $"VM: {item.DestTarget}",
        "hangup" => "Hangup",
        "external" => $"External: {item.DestTarget}",
        _ => item.DestTarget
    };
}
