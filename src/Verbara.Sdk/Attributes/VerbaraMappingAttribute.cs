namespace Verbara.Sdk.Attributes;

/// <summary>
/// Maps a class or property to an Asterisk AMI protocol field name.
/// Used by source generators for AOT-compatible serialization/deserialization.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class VerbaraMappingAttribute : Attribute
{
    public string Name { get; }

    public VerbaraMappingAttribute(string name)
    {
        Name = name;
    }
}
