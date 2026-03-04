using System.Globalization;
using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

/// <summary>
/// AMI UpdateConfig action. Modifies Asterisk configuration files at runtime.
/// Uses a fluent builder to generate the numbered Action-NNNNNN / Cat-NNNNNN /
/// Var-NNNNNN / Value-NNNNNN headers that Asterisk requires.
/// </summary>
[AsteriskMapping("UpdateConfig")]
public sealed class UpdateConfigAction : ManagerAction, IHasExtraFields
{
    private readonly List<ConfigOperation> _operations = [];

    public string? SrcFilename { get; set; }
    public string? DstFilename { get; set; }
    public string? Reload { get; set; }

    /// <summary>Add a NewCat operation to create a new category/section.</summary>
    public UpdateConfigAction AddNewCategory(string category, string? template = null)
    {
        _operations.Add(new ConfigOperation("NewCat", category, template, null));
        return this;
    }

    /// <summary>Add an Append operation to append a variable to a category.</summary>
    public UpdateConfigAction AddAppend(string category, string variable, string value)
    {
        _operations.Add(new ConfigOperation("Append", category, variable, value));
        return this;
    }

    /// <summary>Add an Update operation to update a variable in a category.</summary>
    public UpdateConfigAction AddUpdate(string category, string variable, string value)
    {
        _operations.Add(new ConfigOperation("Update", category, variable, value));
        return this;
    }

    /// <summary>Add a Delete operation to delete a variable from a category.</summary>
    public UpdateConfigAction AddDelete(string category, string variable)
    {
        _operations.Add(new ConfigOperation("Delete", category, variable, null));
        return this;
    }

    /// <summary>Add a DelCat operation to delete an entire category.</summary>
    public UpdateConfigAction AddDeleteCategory(string category)
    {
        _operations.Add(new ConfigOperation("DelCat", category, null, null));
        return this;
    }

    /// <summary>Add a RenameCat operation to rename a category.</summary>
    public UpdateConfigAction AddRenameCat(string oldCategory, string newCategory)
    {
        _operations.Add(new ConfigOperation("RenameCat", oldCategory, newCategory, null));
        return this;
    }

    /// <inheritdoc />
    public IEnumerable<KeyValuePair<string, string>> GetExtraFields()
    {
        for (var i = 0; i < _operations.Count; i++)
        {
            var op = _operations[i];
            var num = i.ToString("D6", CultureInfo.InvariantCulture);

            yield return new($"Action-{num}", op.Action);
            yield return new($"Cat-{num}", op.Category);

            if (op.Variable is not null)
                yield return new($"Var-{num}", op.Variable);

            if (op.Value is not null)
                yield return new($"Value-{num}", op.Value);
        }
    }

    private sealed record ConfigOperation(string Action, string Category, string? Variable, string? Value);
}
