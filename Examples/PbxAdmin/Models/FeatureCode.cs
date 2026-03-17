namespace PbxAdmin.Models;

public sealed record FeatureCode
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record ParkingLotConfig
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "default";
    public int ParkingStartSlot { get; set; } = 701;
    public int ParkingEndSlot { get; set; } = 720;
    public int ParkingTimeout { get; set; } = 45;
    public string MusicOnHold { get; set; } = "default";
    public string Context { get; set; } = "parkedcalls";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
