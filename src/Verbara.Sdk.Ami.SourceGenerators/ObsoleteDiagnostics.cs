#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Verbara.Sdk.Ami.SourceGenerators;

/// <summary>
/// The obsolete diagnostics a generated file must suppress: one per distinct id among the symbols that file binds,
/// and none when it binds no obsolete symbol. A blanket <c>CS0618</c> would outlive the symbols it was written for
/// and would not cover an <c>[Obsolete]</c> that carries a <c>DiagnosticId</c>.
/// </summary>
internal static class ObsoleteDiagnostics
{
    private const string ObsoleteAttributeFqn = "System.ObsoleteAttribute";

    private static readonly string[] None = [];

    /// <summary>
    /// The diagnostic id that referencing <paramref name="symbol"/> raises, or <see langword="null"/> when it is not
    /// obsolete: the attribute's <c>DiagnosticId</c> when it names one, <c>CS0612</c> when there is no message
    /// (a bare <c>[Obsolete]</c>, <c>[Obsolete(null)]</c>, and <c>[Obsolete(null, true)]</c>, which the compiler still
    /// reports as a warning), and <c>CS0618</c> for one with a message. An <c>[Obsolete("...", true)]</c> raises
    /// <c>CS0619</c>, an error no pragma can suppress, so it yields nothing.
    /// </summary>
    public static string? IdOf(ISymbol? symbol)
    {
        if (symbol is null)
            return null;

        var attr = symbol.GetAttributes()
            .FirstOrDefault(static a => a.AttributeClass?.ToDisplayString() == ObsoleteAttributeFqn);
        if (attr is null)
            return null;

        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "DiagnosticId" && named.Value.Value is string id && !string.IsNullOrWhiteSpace(id))
                return id;
        }

        // With no message the compiler raises the CS0612 warning, even when error is true.
        if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Value is null)
            return "CS0612";

        if (attr.ConstructorArguments.Length >= 2 && attr.ConstructorArguments[1].Value is true)
            return null;

        return "CS0618";
    }

    /// <summary>The distinct ids that referencing any of <paramref name="symbols"/> raises, sorted ordinally.</summary>
    public static string[] IdsOf(params ISymbol?[] symbols)
    {
        var ids = symbols.Select(IdOf).Where(static id => id is not null).Select(static id => id!).ToArray();
        return ids.Length == 0
            ? None
            : ids.Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Appends one <c>#pragma warning disable</c> per distinct id, in ordinal order; nothing when there is none.</summary>
    public static void AppendPragmas(StringBuilder sb, IEnumerable<string> ids)
    {
        foreach (var id in ids.Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal))
            sb.AppendLine($"#pragma warning disable {id} // an obsolete symbol this generated code still binds");
    }
}
