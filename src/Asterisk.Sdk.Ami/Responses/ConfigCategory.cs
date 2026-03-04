namespace Asterisk.Sdk.Ami.Responses;

/// <summary>
/// Represents a parsed configuration category from a GetConfig AMI response.
/// </summary>
public sealed record ConfigCategory(string Name, Dictionary<string, string> Variables);
