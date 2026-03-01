namespace Asterisk.NetAot.Abstractions.Attributes;

/// <summary>
/// Maps a class or property to an Asterisk AMI protocol field name.
/// Used by source generators for AOT-compatible serialization/deserialization.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class AsteriskMappingAttribute : Attribute
{
    public string Name { get; }

    public AsteriskMappingAttribute(string name)
    {
        Name = name;
    }
}
