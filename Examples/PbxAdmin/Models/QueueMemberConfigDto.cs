namespace PbxAdmin.Models;

public sealed record QueueMemberConfigDto
{
    public int Id { get; set; }
    public int QueueConfigId { get; set; }
    public string Interface { get; set; } = "";
    public string? MemberName { get; set; }
    public string? StateInterface { get; set; }
    public int Penalty { get; set; }
    public int Paused { get; set; }
}
