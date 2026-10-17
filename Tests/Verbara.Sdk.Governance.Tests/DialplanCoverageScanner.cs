using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Detector for "every extension the functional suite dials into <c>[test-functional]</c> exists there": it reads the
/// extensions that context defines (its own <c>exten</c> lines, every context it includes, and the parking lots
/// <c>res_parking.conf</c> registers in one of them) and every dial into the context under
/// <c>Tests/Verbara.Sdk.FunctionalTests</c>, and reports each dial whose extension is not defined, naming its file and
/// line.
/// </summary>
/// <remarks>
/// <para>
/// A dial into a missing extension fails inside Asterisk, quietly: the originate is refused, no event arrives, and a
/// test that waits for the event and returns when it does not is reported as passed. Ten such extensions were dialled
/// by the suite at once, behind exactly that return, and no run showed it.
/// </para>
/// <para>
/// A dial is read in every spelling the suite uses: a <c>Local/N@test-functional</c> channel in a string literal or an
/// interpolated string; an object initializer that sets <c>Context = "test-functional"</c> and <c>Exten = …</c>
/// (Originate, Redirect, Atxfer, any action); and a call that passes <c>"test-functional"</c> as an argument, whose
/// extension is the next positional argument or the one named <c>exten</c>. A value is resolved through literals,
/// constants, locals, concatenation, interpolation and the parameters of a method in the same file, whose call sites
/// supply the values.
/// </para>
/// <para>
/// The scan fails closed. A dial whose extension it cannot resolve is a violation unless it is on
/// <see cref="Unresolvable"/> with its reason, and so is any string in the suite that mentions <c>test-functional</c>
/// in a shape the scan does not read: an under-report would read exactly like a clean result. A number dialled on
/// purpose because it does not exist is on <see cref="DeliberatelyUndefined"/> with the file that dials it and its
/// reason, so the same number dialled from any other file is a violation; an entry of either list that no longer
/// matches the tree is a violation too. A local that is assigned again anywhere in its scope, or passed by
/// <c>ref</c> or <c>out</c>, does not resolve to its initializer: it is unresolvable.
/// </para>
/// <para>
/// A string given to a named attribute argument (<c>Skip = "…"</c>, <c>DisplayName = "…"</c>) never reaches a test body,
/// so it is not a dial and not a mention. A positional attribute argument (<c>[InlineData("…")]</c>) is a value a
/// theory runs with, and is scanned like any other string.
/// </para>
/// </remarks>
internal static partial class DialplanCoverageScanner
{
    /// <summary>The functional dialplan, relative to the repository root.</summary>
    public const string DialplanPath = "docker/functional/asterisk-config/extensions.conf";

    /// <summary>The functional parking configuration, relative to the repository root.</summary>
    public const string ParkingPath = "docker/functional/asterisk-config/res_parking.conf";

    /// <summary>The functional suite, relative to the repository root.</summary>
    public const string SuitePath = "Tests/Verbara.Sdk.FunctionalTests";

    /// <summary>The dialplan context the suite dials into.</summary>
    public const string Context = "test-functional";

    /// <summary>Numbers a test dials because they do not exist: the file that dials each one, and the reason.</summary>
    public static readonly IReadOnlyList<DeliberateDial> DeliberatelyUndefined =
    [
        new(SuitePath + "/Layer5_Integration/Bridge/TransferTests.cs", "9999",
            "TransferTests.BlindTransfer_ToNonExistentExtension_ShouldFail redirects a live call to a number that does not exist, to observe what Asterisk does with it"),
    ];

    /// <summary>Dials whose extension is a run-time value, each with the reason.</summary>
    public static readonly IReadOnlyList<UnresolvableDial> Unresolvable =
    [
        new(SuitePath + "/Layer5_Integration/Parking/ParkingTests.cs", "parkSlot",
            "the parking space Asterisk assigned to the call this test parked, read from its ParkedCall event; res_parking.conf registers spaces 751-770 in a context [test-functional] includes"),
    ];

    private const long MaximumFileBytes = 1024 * 1024;

    /// <summary>A number a file dials on purpose because <c>[test-functional]</c> does not define it.</summary>
    public sealed record DeliberateDial(string Path, string Extension, string Reason);

    /// <summary>A dial whose extension the scan is told it cannot resolve: a file and the extension's expression.</summary>
    public sealed record UnresolvableDial(string Path, string Expression, string Reason);

    /// <summary>One dial into the context. <see cref="Extensions"/> is null when the scan could not resolve it.</summary>
    public sealed record Dial(string Path, int Line, string Form, string Expression, IReadOnlyList<string>? Extensions)
    {
        public override string ToString() => $"{Path}:{Line}: {Form} {Expression}";
    }

    /// <summary>The outcome of a scan.</summary>
    public sealed record Report(IReadOnlySet<string> Defined, IReadOnlyList<Dial> Dials, IReadOnlyList<string> Violations)
    {
        public bool IsClean => Violations.Count == 0;

        public override string ToString() => Violations.Count == 0
            ? $"clean: {Dials.Count} dials into [{Context}], {Defined.Count} extensions defined"
            : string.Join(Environment.NewLine, Violations);
    }

    /// <summary>The spellings of a dial, as <see cref="Dial.Form"/> names them.</summary>
    public static class Forms
    {
        public const string LocalChannel = "Local channel";
        public const string InterpolatedLocalChannel = "interpolated Local channel";
        public const string ContextExten = "Context/Exten initializer";
        public const string PositionalArgument = "positional argument";
        public const string NamedArgument = "named argument";
    }

    /// <summary>Scans the checkout rooted at <paramref name="repoRoot"/> against this tree's allowlists.</summary>
    public static Report Scan(string repoRoot) => Scan(repoRoot, DeliberatelyUndefined, Unresolvable);

    /// <summary>Scans the checkout rooted at <paramref name="repoRoot"/> against the allowlists given.</summary>
    public static Report Scan(
        string repoRoot,
        IReadOnlyList<DeliberateDial> deliberatelyUndefined,
        IReadOnlyList<UnresolvableDial> unresolvable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(deliberatelyUndefined);
        ArgumentNullException.ThrowIfNull(unresolvable);
        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"The repository root '{repoRoot}' does not exist.");

        var violations = new List<string>();
        var defined = ReadDefined(repoRoot, violations);

        var dials = new List<Dial>();
        var suite = Path.Join(repoRoot, SuitePath);
        if (Directory.Exists(suite))
        {
            foreach (var file in EnumerateSources(suite))
                dials.AddRange(FindDials(File.ReadAllText(file), ToRelative(repoRoot, file), violations));
        }

        if (dials.Count == 0)
            violations.Add($"{SuitePath}: found no dial into [{Context}] — the walk read nothing, so it proves nothing");

        var usedUnresolvable = new HashSet<UnresolvableDial>();
        var usedUndefined = new HashSet<DeliberateDial>();
        foreach (var dial in dials)
        {
            if (dial.Extensions is null)
            {
                var allowed = unresolvable.FirstOrDefault(u =>
                    string.Equals(u.Path, dial.Path, StringComparison.Ordinal) && string.Equals(u.Expression, dial.Expression, StringComparison.Ordinal));
                if (allowed is not null)
                    usedUnresolvable.Add(allowed);
                else
                    violations.Add($"{dial}: the extension cannot be resolved statically — resolve it, or list the site as unresolvable with its reason");
                continue;
            }

            foreach (var extension in dial.Extensions)
            {
                if (IsDefined(extension, defined))
                    continue;
                var deliberate = deliberatelyUndefined.FirstOrDefault(d =>
                    string.Equals(d.Path, dial.Path, StringComparison.Ordinal) && string.Equals(d.Extension, extension, StringComparison.Ordinal));
                if (deliberate is not null)
                {
                    usedUndefined.Add(deliberate);
                    continue;
                }

                violations.Add($"{dial}: dials {extension}, which [{Context}] does not define ({DialplanPath}, its includes and {ParkingPath})");
            }
        }

        foreach (var entry in deliberatelyUndefined)
        {
            if (IsDefined(entry.Extension, defined))
                violations.Add($"{entry.Path}: {entry.Extension} is listed as deliberately undefined, but [{Context}] defines it: the tests that dial it no longer reach a missing number");
            else if (!usedUndefined.Contains(entry))
                violations.Add($"{entry.Path}: {entry.Extension} is listed as deliberately undefined, but no dial there reaches it: remove the entry");
        }

        foreach (var entry in unresolvable.Where(u => !usedUnresolvable.Contains(u)))
            violations.Add($"{entry.Path}: '{entry.Expression}' is listed as unresolvable, but no dial there has that extension: remove the entry");

        return new Report(defined, dials, violations);
    }

    /// <summary>
    /// The extensions <c>[test-functional]</c> defines: its <c>exten</c> lines, those of every context it includes, and
    /// the parking extension and spaces of every lot whose context is one of them. Patterns are kept as written.
    /// </summary>
    public static IReadOnlySet<string> ReadDefined(string repoRoot, List<string> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        var contexts = new Dictionary<string, DialplanContext>(StringComparer.Ordinal);

        var dialplan = Path.Join(repoRoot, DialplanPath);
        if (!File.Exists(dialplan))
            violations.Add($"{DialplanPath}: missing");
        else
            ReadDialplan(File.ReadAllText(dialplan), contexts);

        var parking = Path.Join(repoRoot, ParkingPath);
        if (File.Exists(parking))
            ReadParking(File.ReadAllText(parking), contexts);

        if (!contexts.ContainsKey(Context))
            violations.Add($"{DialplanPath}: defines no [{Context}] context");

        var defined = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Context]);
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!visited.Add(name))
                continue;
            if (!contexts.TryGetValue(name, out var context))
            {
                violations.Add($"[{name}] is included by a context [{Context}] reaches, but neither {DialplanPath} nor {ParkingPath} defines it");
                continue;
            }

            defined.UnionWith(context.Extensions);
            foreach (var include in context.Includes)
                pending.Push(include);
        }

        return defined;
    }

    /// <summary>Whether <paramref name="extension"/> is one of <paramref name="defined"/> or matches one of its patterns.</summary>
    public static bool IsDefined(string extension, IReadOnlySet<string> defined)
    {
        ArgumentNullException.ThrowIfNull(defined);
        if (defined.Contains(extension))
            return true;

        return defined.Where(d => d.StartsWith('_')).Any(pattern => PatternToRegex(pattern).IsMatch(extension));
    }

    /// <summary>Every dial into the context in one C# source, plus a violation for every mention the scan cannot read.</summary>
    public static IReadOnlyList<Dial> FindDials(string source, string path, List<string> violations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(violations);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var dials = new List<Dial>();
        var classified = new HashSet<SyntaxNode>();

        foreach (var node in root.DescendantNodes())
        {
            if (IsNamedAttributeArgument(node))
            {
                classified.Add(node);
                continue;
            }

            switch (node)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    var literalMatches = LocalChannelPattern().Matches(literal.Token.ValueText);
                    if (literalMatches.Count > 0)
                        classified.Add(literal);
                    foreach (Match match in literalMatches)
                        dials.Add(new Dial(path, LineOf(literal), Forms.LocalChannel, match.Value, [match.Groups[1].Value]));

                    break;

                case InterpolatedStringExpressionSyntax interpolated:
                    FindInterpolatedDials(interpolated, path, root, dials, classified);
                    break;

                case InitializerExpressionSyntax initializer when initializer.IsKind(SyntaxKind.ObjectInitializerExpression)
                                                                 || initializer.IsKind(SyntaxKind.WithInitializerExpression):
                    FindInitializerDial(initializer, path, root, dials, classified);
                    break;

                case ArgumentListSyntax arguments:
                    FindArgumentDial(arguments, path, root, dials, classified);
                    break;
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            var text = node switch
            {
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
                InterpolatedStringExpressionSyntax interpolated => interpolated.ToString(),
                _ => null,
            };
            if (text is null || !text.Contains(Context, StringComparison.Ordinal) || classified.Contains(node))
                continue;
            if (node is LiteralExpressionSyntax && node.Ancestors().OfType<InterpolatedStringExpressionSyntax>().Any(classified.Contains))
                continue;

            violations.Add($"{path}:{LineOf(node)}: {node} mentions {Context} in a shape the dialplan guard does not read — "
                + "spell the dial as Local/N@" + Context + ", a Context/Exten pair or a context argument, or teach the guard the new shape");
        }

        return dials;
    }

    /// <summary>
    /// Whether <paramref name="node"/> is a string inside a named attribute argument (<c>Skip = "…"</c>), which never
    /// reaches a test body. A positional or constructor-named attribute argument (<c>[InlineData("…")]</c>) can be a
    /// theory's input and is scanned.
    /// </summary>
    private static bool IsNamedAttributeArgument(SyntaxNode node) =>
        node is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax
        && node.Ancestors().OfType<AttributeArgumentSyntax>().FirstOrDefault() is { NameEquals: not null };

    private static void FindInterpolatedDials(InterpolatedStringExpressionSyntax interpolated, string path, SyntaxNode root,
        List<Dial> dials, HashSet<SyntaxNode> classified)
    {
        // Each hole becomes a private-use character so the channel pattern can find where the extension sits.
        var template = new StringBuilder();
        var holes = new List<InterpolationSyntax>();
        foreach (var content in interpolated.Contents)
        {
            if (content is InterpolatedStringTextSyntax text)
            {
                template.Append(text.TextToken.ValueText);
            }
            else if (content is InterpolationSyntax hole)
            {
                template.Append((char)(0xE000 + holes.Count));
                holes.Add(hole);
            }
        }

        var matches = LocalChannelPattern().Matches(template.ToString());
        if (matches.Count == 0)
            return;

        classified.Add(interpolated);
        foreach (Match match in matches)
        {
            var exten = match.Groups[1].Value;
            var used = exten.Where(c => c >= 0xE000 && c < 0xE000 + holes.Count).Select(c => holes[c - 0xE000]).ToList();
            var expression = used.Count == 1 && exten.Length == 1
                ? used[0].Expression.ToString()
                : interpolated.ToString();

            List<string>? values = [string.Empty];
            foreach (var c in exten)
            {
                IReadOnlyList<string?>? part = c >= 0xE000 && c < 0xE000 + holes.Count
                    ? Resolve(holes[c - 0xE000].Expression, root, depth: 0)
                    : [new string(c, 1)];
                if (part is null || part.Count == 0 || part.Any(v => v is null))
                {
                    values = null;
                    break;
                }

                values = values.SelectMany(v => part.Select(p => v + p)).ToList();
            }

            dials.Add(new Dial(path, LineOf(interpolated), Forms.InterpolatedLocalChannel, expression, values?.Distinct(StringComparer.Ordinal).ToList()));
        }
    }

    private static void FindInitializerDial(InitializerExpressionSyntax initializer, string path, SyntaxNode root,
        List<Dial> dials, HashSet<SyntaxNode> classified)
    {
        ExpressionSyntax? context = null;
        ExpressionSyntax? exten = null;
        foreach (var assignment in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not IdentifierNameSyntax name)
                continue;
            if (name.Identifier.ValueText == "Context")
                context = assignment.Right;
            else if (name.Identifier.ValueText == "Exten")
                exten = assignment.Right;
        }

        if (context is null || !IsContextLiteral(context))
            return;

        classified.Add(context);
        if (exten is null)
        {
            dials.Add(new Dial(path, LineOf(context), Forms.ContextExten, "(no Exten)", null));
            return;
        }

        dials.Add(ResolvedDial(path, exten, Forms.ContextExten, root));
    }

    private static void FindArgumentDial(ArgumentListSyntax arguments, string path, SyntaxNode root,
        List<Dial> dials, HashSet<SyntaxNode> classified)
    {
        var list = arguments.Arguments;
        for (var i = 0; i < list.Count; i++)
        {
            if (!IsContextLiteral(list[i].Expression))
                continue;

            classified.Add(list[i].Expression);
            if (list[i].NameColon is not null)
            {
                var named = list.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "exten");
                dials.Add(named is null
                    ? new Dial(path, LineOf(list[i]), Forms.NamedArgument, "(no exten argument)", null)
                    : ResolvedDial(path, named.Expression, Forms.NamedArgument, root));
            }
            else
            {
                var next = i + 1 < list.Count && list[i + 1].NameColon is null ? list[i + 1] : null;
                dials.Add(next is null
                    ? new Dial(path, LineOf(list[i]), Forms.PositionalArgument, "(no argument after the context)", null)
                    : ResolvedDial(path, next.Expression, Forms.PositionalArgument, root));
            }
        }
    }

    private static Dial ResolvedDial(string path, ExpressionSyntax exten, string form, SyntaxNode root)
    {
        var values = Resolve(exten, root, depth: 0);
        IReadOnlyList<string>? extensions = values is null || values.Any(v => v is null)
            ? null
            : values.Select(v => v!).Distinct(StringComparer.Ordinal).ToList();
        return new Dial(path, LineOf(exten), form, exten.ToString(), extensions);
    }

    private static bool IsContextLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
        && literal.Token.ValueText == Context;

    /// <summary>
    /// The values <paramref name="expression"/> can take, or null when it cannot be resolved without running the code.
    /// A null element is a null value (a parameter's default), which a dial must not carry.
    /// </summary>
    private static IReadOnlyList<string?>? Resolve(ExpressionSyntax expression, SyntaxNode root, int depth)
    {
        if (depth > 8)
            return null;

        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return [literal.Token.ValueText];
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression):
                return [null];
            case ParenthesizedExpressionSyntax parenthesized:
                return Resolve(parenthesized.Expression, root, depth + 1);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
            {
                var left = Resolve(binary.Left, root, depth + 1);
                var right = Resolve(binary.Right, root, depth + 1);
                if (left is null || right is null || left.Any(v => v is null) || right.Any(v => v is null))
                    return null;
                return left.SelectMany(l => right.Select(r => (string?)(l + r))).ToList();
            }

            case InterpolatedStringExpressionSyntax interpolated:
            {
                IReadOnlyList<string?> values = [string.Empty];
                foreach (var content in interpolated.Contents)
                {
                    IReadOnlyList<string?>? part = content switch
                    {
                        InterpolatedStringTextSyntax text => [text.TextToken.ValueText],
                        InterpolationSyntax hole when hole.AlignmentClause is null && hole.FormatClause is null => Resolve(hole.Expression, root, depth + 1),
                        _ => null,
                    };
                    if (part is null || part.Any(v => v is null))
                        return null;
                    values = values.SelectMany(v => part.Select(p => (string?)(v + p))).ToList();
                }

                return values;
            }

            case IdentifierNameSyntax identifier:
                return ResolveIdentifier(identifier, root, depth);
            default:
                return null;
        }
    }

    private static IReadOnlyList<string?>? ResolveIdentifier(IdentifierNameSyntax identifier, SyntaxNode root, int depth)
    {
        var name = identifier.Identifier.ValueText;

        // A local, or a parameter of the nearest enclosing method, local function or lambda that declares the name.
        foreach (var scope in identifier.Ancestors())
        {
            var local = scope.ChildNodes().OfType<LocalDeclarationStatementSyntax>()
                .Concat(scope.ChildNodes().OfType<GlobalStatementSyntax>().Select(g => g.Statement).OfType<LocalDeclarationStatementSyntax>())
                .SelectMany(d => d.Declaration.Variables)
                .FirstOrDefault(v => v.Identifier.ValueText == name);
            if (local is not null)
                return local.Initializer is null || IsWrittenAgain(scope, name)
                    ? null
                    : Resolve(local.Initializer.Value, root, depth + 1);

            SeparatedSyntaxList<ParameterSyntax>? parameters = scope switch
            {
                BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
                LocalFunctionStatementSyntax function => function.ParameterList.Parameters,
                ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters,
                _ => null,
            };
            if (parameters is { } declared && declared.IndexOf(p => p.Identifier.ValueText == name) is var index and >= 0)
            {
                var methodName = scope switch
                {
                    MethodDeclarationSyntax method => method.Identifier.ValueText,
                    LocalFunctionStatementSyntax function => function.Identifier.ValueText,
                    _ => null,
                };
                return methodName is null ? null : ResolveParameter(methodName, declared[index], index, root, depth);
            }

            if (scope is TypeDeclarationSyntax)
                break;
        }

        // A field or constant of a type in the same file.
        var field = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) || f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            .SelectMany(f => f.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.ValueText == name);
        return field?.Initializer is null ? null : Resolve(field.Initializer.Value, root, depth + 1);
    }

    /// <summary>
    /// Whether the local <paramref name="name"/> is assigned anywhere in <paramref name="scope"/> after its declaration,
    /// or passed by <c>ref</c> or <c>out</c>: then its initializer is not the value every use sees. An object initializer's
    /// <c>Name = …</c> sets a member, not the local, and does not count.
    /// </summary>
    private static bool IsWrittenAgain(SyntaxNode scope, string name)
    {
        static bool Names(ExpressionSyntax expression, string name) =>
            expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == name;

        return scope.DescendantNodes().Any(node => node switch
        {
            AssignmentExpressionSyntax assignment => assignment.Parent is not InitializerExpressionSyntax && Names(assignment.Left, name),
            ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None) && !argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword)
                && Names(argument.Expression, name),
            RefExpressionSyntax reference => Names(reference.Expression, name),
            _ => false,
        });
    }

    /// <summary>The values a parameter takes at every call site of its method in the same file.</summary>
    private static List<string?>? ResolveParameter(string methodName, ParameterSyntax parameter, int index, SyntaxNode root, int depth)
    {
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText == methodName,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == methodName,
                _ => false,
            })
            .ToList();
        if (calls.Count == 0)
            return null;

        var values = new List<string?>();
        foreach (var call in calls)
        {
            var arguments = call.ArgumentList.Arguments;
            var argument = arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter.Identifier.ValueText)
                ?? (index < arguments.Count && arguments.Take(index + 1).All(a => a.NameColon is null) ? arguments[index] : null);
            var resolved = argument is not null
                ? Resolve(argument.Expression, root, depth + 1)
                : parameter.Default is not null ? Resolve(parameter.Default.Value, root, depth + 1) : null;
            if (resolved is null)
                return null;
            values.AddRange(resolved);
        }

        return values;
    }

    private static void ReadDialplan(string text, Dictionary<string, DialplanContext> contexts)
    {
        DialplanContext? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('['))
            {
                var name = line[1..line.IndexOf(']', StringComparison.Ordinal)].Trim();
                current = GetOrAdd(contexts, name);
                continue;
            }

            if (current is null)
                continue;

            var match = DirectivePattern().Match(line);
            if (!match.Success)
                continue;

            var value = match.Groups["value"].Value.Trim();
            if (match.Groups["key"].Value == "exten")
            {
                var extension = value.Split(',')[0].Trim();
                var slash = extension.IndexOf('/', StringComparison.Ordinal);
                current.Extensions.Add(slash >= 0 ? extension[..slash] : extension);
            }
            else
            {
                current.Includes.Add(value.Split(',')[0].Trim());
            }
        }
    }

    private static void ReadParking(string text, Dictionary<string, DialplanContext> contexts)
    {
        var lots = new List<Dictionary<string, string>>();
        Dictionary<string, string>? lot = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('['))
            {
                var name = line[1..line.IndexOf(']', StringComparison.Ordinal)].Trim();
                lot = name == "general" ? null : new Dictionary<string, string>(StringComparer.Ordinal);
                if (lot is not null)
                    lots.Add(lot);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (lot is null || equals < 0)
                continue;
            lot[line[..equals].Trim()] = line[(equals + 1)..].TrimStart('>').Trim();
        }

        foreach (var options in lots)
        {
            // res_parking's own default for a lot's context.
            var context = GetOrAdd(contexts, options.GetValueOrDefault("context", "parkedcalls"));
            if (options.TryGetValue("parkext", out var parkext) && parkext.Length > 0)
                context.Extensions.Add(parkext);
            if (options.TryGetValue("parkpos", out var parkpos) && ParkPosPattern().Match(parkpos) is { Success: true } range)
            {
                var first = int.Parse(range.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var last = int.Parse(range.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                for (var space = first; space <= last; space++)
                    context.Extensions.Add(space.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    private static DialplanContext GetOrAdd(Dictionary<string, DialplanContext> contexts, string name)
    {
        if (!contexts.TryGetValue(name, out var context))
        {
            context = new DialplanContext();
            contexts[name] = context;
        }

        return context;
    }

    private static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == ';' && (i == 0 || line[i - 1] != '\\'))
                return line[..i];
        }

        return line;
    }

    /// <summary>An Asterisk extension pattern (<c>_NXX.</c>) as a regular expression.</summary>
    private static Regex PatternToRegex(string pattern)
    {
        var regex = new StringBuilder("^");
        for (var i = 1; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (char.ToUpperInvariant(c))
            {
                case 'X': regex.Append("[0-9]"); break;
                case 'Z': regex.Append("[1-9]"); break;
                case 'N': regex.Append("[2-9]"); break;
                case '.': regex.Append(".+"); break;
                case '!': regex.Append(".*"); break;
                case '[':
                    var close = pattern.IndexOf(']', i);
                    if (close < 0)
                        return new Regex("(?!)");
                    regex.Append('[').Append(pattern[(i + 1)..close].Replace("\\", "\\\\", StringComparison.Ordinal)).Append(']');
                    i = close;
                    break;
                default: regex.Append(Regex.Escape(new string(c, 1))); break;
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static IEnumerable<string> EnumerateSources(string suite) =>
        Directory.EnumerateFiles(suite, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(suite, file).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "TestResults"))
            .Where(file => new FileInfo(file).Length <= MaximumFileBytes)
            .Order(StringComparer.Ordinal);

    private static string ToRelative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private sealed class DialplanContext
    {
        public HashSet<string> Extensions { get; } = new(StringComparer.Ordinal);

        public List<string> Includes { get; } = [];
    }

    // The context ends the match, or is followed by a Local channel's instance suffix (Local/100@test-functional-0000002a;2).
    [GeneratedRegex(@"Local/([^@\s/""',]+)@test-functional(?=$|[^\w-]|-[0-9a-fA-F]*(?:;|$))", RegexOptions.CultureInvariant)]
    private static partial Regex LocalChannelPattern();

    [GeneratedRegex(@"^(?<key>exten|include)\s*=>?\s*(?<value>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DirectivePattern();

    [GeneratedRegex(@"^\s*(\d+)\s*-\s*(\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParkPosPattern();
}
