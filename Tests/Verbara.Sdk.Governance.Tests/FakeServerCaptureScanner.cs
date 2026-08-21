using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A fake-server member that hands tests the live collection its own receive loop writes.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan),
/// <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record FakeServerCaptureViolation(string Path, int Line, string Rule, string Member, string Detail);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports in-process fake servers that
/// expose a captured-frame collection tests can read while the receive loop is still writing it.
/// </summary>
/// <remarks>
/// <para>
/// The receive loop of every fake in this repo runs on its own thread. Handing a test the backing
/// <c>List&lt;T&gt;</c> makes every assertion a read of a collection under concurrent mutation: at
/// best a count that changes between two assertions in the same test, at worst an
/// <see cref="InvalidOperationException"/> out of the enumerator. The fix is always the same shape —
/// keep the list private, expose <c>IReadOnlyList&lt;T&gt;</c>, and return a copy taken under the
/// lock the loop writes under.
/// </para>
/// <para>
/// The discriminator that makes this detectable without an ignore list is <b>who writes</b>. A
/// capture is written by the fake itself (<c>_received.Add(frame)</c> inside the session handler);
/// configuration — the events or audio frames a test queues for delivery — is written by the test
/// before the server starts, and inside the type is either only <em>read</em> or seeded once in the
/// constructor, where no reader can be racing it. So the rules fire only on members the declaring
/// type mutates outside its constructor, which is why <c>EventsToSend</c>, <c>AudioFramesToSend</c>
/// and <c>ResultMessages</c> can stay plain writable lists and never be reported.
/// </para>
/// <para>
/// Two rules:
/// (a) <b>MutableCapture</b> — an exposed member declared as a mutable collection which the
///     declaring type mutates (<c>Add</c>/<c>Enqueue</c>/<c>Clear</c>/indexer assignment/…).
/// (b) <b>LiveCaptureAlias</b> — an exposed member declared as a read-only collection whose getter
///     hands back a private mutable field <em>bare</em>, with no <c>ToArray()</c>/<c>ToList()</c>
///     between. This is the same defect wearing an interface: the caller cannot add to it, but it
///     still enumerates a list another thread is appending to. Without this rule the guard would be
///     satisfied by widening the property type and changing nothing.
/// </para>
/// <para>
/// Detection is syntactic, so C# snippets embedded as ordinary string literals — this detector's own
/// test fixtures included — can never self-flag. Known blind spot: a read-only auto-property assigned
/// the private list in a constructor. Nothing in the repo has that shape, and catching it needs the
/// symbol model rather than the syntax tree.
/// </para>
/// </remarks>
internal static class FakeServerCaptureScanner
{
    /// <summary>
    /// Type-name suffixes that mark an in-process fake server: <c>*FakeServer</c> and the shared
    /// <c>WebSocketTestServer</c> substrate they all run on.
    /// </summary>
    private static readonly string[] FakeServerSuffixes = ["FakeServer", "TestServer"];

    private static readonly HashSet<string> MutableCollectionTypes = new(StringComparer.Ordinal)
    {
        "List", "IList", "ICollection", "Collection", "ObservableCollection",
        "Dictionary", "IDictionary", "SortedList", "SortedDictionary",
        "HashSet", "ISet", "SortedSet", "Queue", "Stack",
        "ConcurrentBag", "ConcurrentQueue", "ConcurrentStack", "ConcurrentDictionary",
    };

    private static readonly HashSet<string> ReadOnlyCollectionTypes = new(StringComparer.Ordinal)
    {
        "IReadOnlyList", "IReadOnlyCollection", "IReadOnlyDictionary", "IReadOnlySet",
        "IEnumerable", "ReadOnlyCollection", "ReadOnlyDictionary",
    };

    /// <summary>Calls that write to the receiver — the signal that a member is a capture.</summary>
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal)
    {
        "Add", "AddRange", "AddOrUpdate", "TryAdd", "Insert", "InsertRange",
        "Enqueue", "Push", "Remove", "RemoveAt", "RemoveRange", "RemoveAll", "Clear",
    };

    /// <summary>Marker used for array types, which are mutable through their indexer.</summary>
    private const string ArrayTypeName = "[]";

    public static IReadOnlyList<FakeServerCaptureViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var violations = new List<FakeServerCaptureViolation>();

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (!IsFakeServerType(type))
                continue;

            var mutated = CollectMutatedNames(type);
            var privateMutableFields = CollectPrivateMutableFields(type);

            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case PropertyDeclarationSyntax property:
                        InspectMember(
                            violations,
                            path,
                            property.Identifier.Text,
                            property.Type,
                            property.Modifiers,
                            property,
                            ReturnedNames(property),
                            mutated,
                            privateMutableFields);
                        break;

                    case FieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables)
                        {
                            InspectMember(
                                violations,
                                path,
                                variable.Identifier.Text,
                                field.Declaration.Type,
                                field.Modifiers,
                                field,
                                [],
                                mutated,
                                privateMutableFields);
                        }

                        break;
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Names of the fake-server types declared in <paramref name="source"/>. The guard's liveness
    /// self-test asserts on this: a scan that walks every file but recognises no fake server at all
    /// is a false green, and a rename is exactly how that would happen quietly.
    /// </summary>
    public static IReadOnlyList<string> FindFakeServerTypes(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(IsFakeServerType)
            .Select(t => t.Identifier.Text)
            .ToArray();
    }

    private static void InspectMember(
        List<FakeServerCaptureViolation> violations,
        string path,
        string name,
        TypeSyntax declaredType,
        SyntaxTokenList modifiers,
        SyntaxNode node,
        IReadOnlyList<string> returnedNames,
        HashSet<string> mutated,
        HashSet<string> privateMutableFields)
    {
        if (!IsExposed(modifiers))
            return;

        var typeName = RootTypeName(declaredType);
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var isMutableType = typeName == ArrayTypeName || MutableCollectionTypes.Contains(typeName);
        var isReadOnlyType = ReadOnlyCollectionTypes.Contains(typeName);

        if (!isMutableType && !isReadOnlyType)
            return;

        // The member is a capture either because the type writes to it under its own name, or
        // because it hands back a private field the type writes to. Both reach the test as the same
        // live collection; only the second needs the alias named in the message.
        var aliased = FindLiveAlias(returnedNames, mutated, privateMutableFields);

        if (isMutableType && (mutated.Contains(name) || aliased is not null))
        {
            violations.Add(new FakeServerCaptureViolation(
                path,
                line,
                "MutableCapture",
                name,
                $"'{name}' is a mutable collection the fake itself writes, handed to tests as-is. The receive " +
                "loop runs on another thread, so every assertion on it is a read of a collection under " +
                "concurrent mutation. Keep the list private and expose IReadOnlyList<T> returning a copy " +
                "taken under the same lock."));
            return;
        }

        if (isReadOnlyType && aliased is not null)
        {
            violations.Add(new FakeServerCaptureViolation(
                path,
                line,
                "LiveCaptureAlias",
                name,
                $"'{name}' is typed read-only but returns the live field '{aliased}' the receive loop " +
                "appends to — the caller cannot add to it, yet still enumerates a collection another " +
                "thread is mutating. Return a copy (ToArray()) taken under the lock."));
        }
    }

    /// <summary>
    /// The private mutable field this member hands back bare, if any — the field must be one the
    /// type writes to outside its constructor, or the member is publishing something inert.
    /// </summary>
    private static string? FindLiveAlias(
        IReadOnlyList<string> returnedNames,
        HashSet<string> mutated,
        HashSet<string> privateMutableFields)
    {
        foreach (var returned in returnedNames)
        {
            if (privateMutableFields.Contains(returned) && mutated.Contains(returned))
                return returned;
        }

        return null;
    }

    private static bool IsFakeServerType(TypeDeclarationSyntax type)
    {
        foreach (var suffix in FakeServerSuffixes)
        {
            if (type.Identifier.Text.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Exposed = reachable from a test: <c>public</c> or <c>internal</c>.</summary>
    private static bool IsExposed(SyntaxTokenList modifiers)
    {
        foreach (var modifier in modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.InternalKeyword))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every name the type writes to <em>while a session is running</em>: the receiver of a mutating
    /// call (<c>x.Add(…)</c>, <c>this.x.Enqueue(…)</c>) and the target of an indexer assignment
    /// (<c>x[i] = …</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two exclusions, both structural rather than by name:
    /// </para>
    /// <para>
    /// <b>Constructor writes are seeding, not capture.</b> Several fakes fill their configuration
    /// list with a default in the constructor — <c>AudioFramesToSend.AddRange(recordedTone)</c>,
    /// <c>ResultMessages.Add(partialTranscript)</c> — so a test that does not care about the payload
    /// still exercises a realistic one. A constructor runs before any caller holds the object, so no
    /// reader can be racing it, and the collection is still test→server configuration afterwards. A
    /// list seeded in the constructor AND written from the session handler is still reported: the
    /// session-handler write is what makes it a capture.
    /// </para>
    /// <para>
    /// <b>A whole-reference assignment is not a mutation.</b> <c>_snapshot = _live.ToArray()</c>
    /// publishes a fresh immutable array — the fix, not the defect.
    /// </para>
    /// </remarks>
    private static HashSet<string> CollectMutatedNames(TypeDeclarationSyntax type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var invocation in type.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access)
                continue;
            if (!MutatingMethods.Contains(access.Name.Identifier.Text))
                continue;
            if (IsInsideConstructor(invocation))
                continue;

            var target = SimpleName(access.Expression);
            if (target is not null)
                names.Add(target);
        }

        foreach (var assignment in type.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not ElementAccessExpressionSyntax element)
                continue;
            if (IsInsideConstructor(assignment))
                continue;

            var target = SimpleName(element.Expression);
            if (target is not null)
                names.Add(target);
        }

        return names;
    }

    private static bool IsInsideConstructor(SyntaxNode node) =>
        node.Ancestors().OfType<ConstructorDeclarationSyntax>().Any();

    private static HashSet<string> CollectPrivateMutableFields(TypeDeclarationSyntax type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            if (IsExposed(field.Modifiers))
                continue;

            var typeName = RootTypeName(field.Declaration.Type);
            if (typeName != ArrayTypeName && !MutableCollectionTypes.Contains(typeName))
                continue;

            foreach (var variable in field.Declaration.Variables)
                names.Add(variable.Identifier.Text);
        }

        return names;
    }

    /// <summary>
    /// Expressions a property getter hands back, reduced to bare member names. Only a bare
    /// identifier (or <c>this.x</c>) counts — anything wrapped in a call, including the
    /// <c>ToArray()</c> that makes a snapshot a snapshot, is not an alias for the live collection.
    /// </summary>
    private static List<string> ReturnedNames(PropertyDeclarationSyntax property)
    {
        var names = new List<string>();

        if (property.ExpressionBody is not null)
            AddIfSimple(names, property.ExpressionBody.Expression);

        foreach (var accessor in property.AccessorList?.Accessors ?? default)
        {
            if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                continue;

            if (accessor.ExpressionBody is not null)
                AddIfSimple(names, accessor.ExpressionBody.Expression);

            if (accessor.Body is null)
                continue;

            foreach (var statement in accessor.Body.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (statement.Expression is not null)
                    AddIfSimple(names, statement.Expression);
            }
        }

        return names;

        static void AddIfSimple(List<string> into, ExpressionSyntax expression)
        {
            var name = SimpleName(expression);
            if (name is not null)
                into.Add(name);
        }
    }

    /// <summary>Reduces <c>x</c> and <c>this.x</c> to <c>x</c>; anything else to <see langword="null"/>.</summary>
    private static string? SimpleName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } =>
            name.Identifier.Text,
        _ => null,
    };

    /// <summary>
    /// The generic type name without arguments or namespace qualification — <c>List&lt;string&gt;</c>
    /// and <c>System.Collections.Generic.List&lt;string&gt;</c> both reduce to <c>List</c>. Arrays
    /// reduce to <see cref="ArrayTypeName"/>; nullable annotations are unwrapped.
    /// </summary>
    private static string RootTypeName(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => RootTypeName(nullable.ElementType),
        ArrayTypeSyntax => ArrayTypeName,
        QualifiedNameSyntax qualified => RootTypeName(qualified.Right),
        GenericNameSyntax generic => generic.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => string.Empty,
    };
}
