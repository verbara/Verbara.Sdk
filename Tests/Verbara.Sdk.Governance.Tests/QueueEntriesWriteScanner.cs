using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A write to a queue's <c>Entries</c> outside the two helpers that keep Live's count of held <c>Uniqueid</c>s.
/// <see cref="Path"/> is a display path, <see cref="Line"/> is 1-based, <see cref="Write"/> the expression's text.
/// </summary>
internal sealed record QueueEntriesWriteViolation(string Path, int Line, string Write);

/// <summary>
/// Pure, I/O-free detector for the queue table's write funnel: in <c>Verbara.Sdk.Live</c>, the queue manager counts how
/// many entries carry each <c>Uniqueid</c>, so a <c>Hangup</c> no entry holds skips the walk of every queue. The count
/// is right only if every write to <c>AsteriskQueue.Entries</c> goes through the store and take helpers, which raise it
/// before an entry is visible and lower it after one is gone. A write anywhere else leaves the count short (a held
/// caller whose <c>Hangup</c> skips the walk: a ghost) or high (one walk too many and a key never freed).
/// </summary>
/// <remarks>
/// <para>
/// A write is an element access on <c>Entries</c> (<c>x.Entries[key]</c>, read or written: the table is read through
/// <c>TryGetValue</c> and <c>GetValueOrDefault</c>) or a call of <c>TryRemove</c>, <c>TryAdd</c>, <c>AddOrUpdate</c>,
/// <c>GetOrAdd</c>, <c>TryUpdate</c> or <c>Clear</c> on it. The receiver is recognised by name: a member access whose
/// name is <c>Entries</c>, or a bare <c>Entries</c> inside the class <c>AsteriskQueue</c>. Syntax only, no types: another
/// type's member access named <c>Entries</c> is reported too (a false red, never a false green); a bare <c>Entries</c> in
/// another class is a member of that class and is not.
/// </para>
/// <para>
/// Writes inside <see cref="Helpers"/> of the class <c>QueueManager</c> are the funnel and are counted, not reported:
/// the guard asserts a floor on them, so renaming the helpers or moving the writes out of them fails it instead of
/// leaving nothing to examine.
/// </para>
/// </remarks>
internal static class QueueEntriesWriteScanner
{
    /// <summary>The two helpers of <c>QueueManager</c> through which every write to <c>Entries</c> goes.</summary>
    public static readonly IReadOnlyList<string> Helpers = ["StoreEntryLocked", "TakeEntryLocked"];

    private static readonly HashSet<string> WriteMethods = new(StringComparer.Ordinal)
    {
        "TryRemove",
        "TryAdd",
        "AddOrUpdate",
        "GetOrAdd",
        "TryUpdate",
        "Clear",
    };

    private const string EntriesName = "Entries";
    private const string OwnerClass = "AsteriskQueue";
    private const string ManagerClass = "QueueManager";

    /// <summary>The writes to <c>Entries</c> in <paramref name="source"/> outside the helpers.</summary>
    public static IReadOnlyList<QueueEntriesWriteViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        return Writes(source)
            .Where(write => !InHelper(write))
            .Select(write => new QueueEntriesWriteViolation(path,
                write.GetLocation().GetLineSpan().StartLinePosition.Line + 1, write.ToString()))
            .ToList();
    }

    /// <summary>How many writes to <c>Entries</c> in <paramref name="source"/> sit inside the helpers.</summary>
    public static int CountHelperWrites(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Writes(source).Count(InHelper);
    }

    private static IEnumerable<ExpressionSyntax> Writes(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ElementAccessExpressionSyntax access when IsEntries(access.Expression):
                    yield return access;
                    break;
                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Expression: var receiver, Name: var method },
                } invocation when WriteMethods.Contains(method.Identifier.Text) && IsEntries(receiver):
                    yield return invocation;
                    break;
            }
        }
    }

    private static bool IsEntries(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text == EntriesName,
        IdentifierNameSyntax bare => bare.Identifier.Text == EntriesName && EnclosingClass(bare) == OwnerClass,
        _ => false,
    };

    private static bool InHelper(SyntaxNode write)
    {
        var method = write.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method is not null
            && Helpers.Contains(method.Identifier.Text)
            && EnclosingClass(method) == ManagerClass
            && !write.Ancestors().TakeWhile(node => node != method)
                .Any(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
    }

    private static string? EnclosingClass(SyntaxNode node) =>
        node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
}
