namespace Asterisk.Sdk.Attributes;

/// <summary>
/// Indicates the minimum Asterisk version that supports this action or event.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AsteriskVersionAttribute : Attribute
{
    public string SinceVersion { get; }

    public AsteriskVersionAttribute(string sinceVersion)
    {
        SinceVersion = sinceVersion;
    }
}
