namespace Asterisk.Sdk;

/// <summary>
/// Allows AMI actions to emit additional dynamic key-value fields
/// that are not statically defined as properties. AOT-safe alternative
/// to reflection for numbered headers like Action-NNNNNN, Cat-NNNNNN, etc.
/// </summary>
public interface IHasExtraFields
{
    /// <summary>Returns extra key-value pairs to append after the static properties.</summary>
    IEnumerable<KeyValuePair<string, string>> GetExtraFields();
}
