using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

/// <summary>
/// Response to the GetConfig AMI action.
/// Parses Category-NNNNNN and Line-NNNNNN-MMMMMM fields into structured categories.
/// </summary>
[AsteriskMapping("GetConfig")]
public sealed class GetConfigResponse : ManagerResponse
{
    private IReadOnlyList<ConfigCategory>? _categories;

    /// <summary>
    /// Parsed configuration categories from the raw AMI response fields.
    /// </summary>
    public IReadOnlyList<ConfigCategory> Categories => _categories ??= ParseCategories();

    private List<ConfigCategory> ParseCategories()
    {
        if (RawFields is null || RawFields.Count == 0)
            return [];

        // Parse Category-NNNNNN fields to build name index
        var categoryNames = new SortedDictionary<string, string>();
        foreach (var (key, value) in RawFields)
        {
            if (key.StartsWith("Category-", StringComparison.OrdinalIgnoreCase) && key.Length > 9)
            {
                var num = key[9..];
                categoryNames[num] = value;
            }
        }

        if (categoryNames.Count == 0)
            return [];

        // Pre-build category objects in order
        var categories = new List<ConfigCategory>(categoryNames.Count);
        var categoryByNum = new Dictionary<string, ConfigCategory>(categoryNames.Count);
        foreach (var (num, name) in categoryNames)
        {
            var cat = new ConfigCategory(name, new Dictionary<string, string>());
            categories.Add(cat);
            categoryByNum[num] = cat;
        }

        // Parse Line-NNNNNN-MMMMMM fields (category num - line num)
        foreach (var (key, value) in RawFields)
        {
            if (!key.StartsWith("Line-", StringComparison.OrdinalIgnoreCase) || key.Length <= 5)
                continue;

            // Find the category number (first segment after "Line-")
            var rest = key[5..];
            var dashIdx = rest.IndexOf('-');
            if (dashIdx <= 0)
                continue;

            var catNum = rest[..dashIdx];
            if (!categoryByNum.TryGetValue(catNum, out var cat))
                continue;

            // Parse "key=value" from the line
            var eqIdx = value.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            var varName = value[..eqIdx].Trim();
            var varValue = value[(eqIdx + 1)..].Trim();
            cat.Variables[varName] = varValue;
        }

        return categories;
    }
}
