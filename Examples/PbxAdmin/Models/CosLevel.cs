namespace PbxAdmin.Models;

public sealed class CosLevel
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Priority { get; set; }
    public string AsteriskContext { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public bool AllowPremium { get; set; }
    public bool AllowMobile { get; set; } = true;
    public bool AllowForwardExternal { get; set; }
    public bool AllowConferenceExternal { get; set; }
    public bool AllowRecordingControl { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
    public List<CosLevelRule> Rules { get; set; } = [];
}

public sealed class CosLevelRule
{
    public int Id { get; set; }
    public int CosLevelId { get; set; }
    public int PatternGroupId { get; set; }
    public string Action { get; set; } = "ALLOW";
    public int Sequence { get; set; }
    public string? PatternGroupName { get; set; }
}

public sealed class CosExtensionOverride
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Extension { get; set; } = "";
    public int? CosLevelId { get; set; }
    public string? PatternOverrides { get; set; }
    public string? Notes { get; set; }
}

public sealed class CosTimeWindow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ServerId { get; set; } = "";
    public int DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}

public sealed class CosTimeOverride
{
    public int Id { get; set; }
    public int CosLevelId { get; set; }
    public int OverrideCosLevelId { get; set; }
    public int TimeWindowId { get; set; }
    public int Priority { get; set; } = 100;
}

public sealed class CosAuditEntry
{
    public string ServerId { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? ChangedBy { get; set; }
}
