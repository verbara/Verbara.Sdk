using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>A hex-dump constant of the shared wire fixture, decoded.</summary>
internal sealed record CapturedConstant(string Name, byte[] Bytes);

/// <summary>A run of byte values a source file writes as literals, and the 1-based line it starts on.</summary>
internal sealed record ByteRun(int Line, byte[] Bytes);

/// <summary>
/// A test source that carries a copy of captured AudioSocket bytes. <see cref="Offset"/> is where in
/// <see cref="Constant"/> the copy starts and <see cref="Length"/> how many bytes it shares with it.
/// </summary>
internal sealed record AudioSocketCaptureViolation(string Path, int Line, string Constant, int Offset, int Length);

/// <summary>
/// Pure, I/O-free detector for ADR-0060 R4: the captured AudioSocket bytes exist in one fixture only.
/// Reads the fixture's hex-dump constants, extracts every run of byte values another source writes as
/// literals, and reports a run that shares at least <see cref="MinimumRun"/> consecutive bytes with a
/// constant.
/// </summary>
/// <remarks>
/// <para>
/// <b>What counts as written bytes.</b> A test hands a parser bytes in one of two shapes, and both are
/// read. (a) A hex dump in a string: pairs separated by spaces, dashes, colons or commas (the probe's
/// own <c>01 00 10</c> form and <c>BitConverter</c>'s <c>01-00-10</c>), or an unseparated even-length
/// token (<c>Convert.FromHexString("010010…")</c>). Adjacent string literals joined with <c>+</c> are
/// folded first, because the fixture writes its audio frame as 21 of them and a copy would be written
/// the same way. (b) An unbroken sequence of integer literals from 0 to 255, casts allowed, in an array
/// or collection initializer or an argument list: <c>new byte[] { 0x01, 0x00, 0x10, … }</c>,
/// <c>[1, 0, 16, …]</c>, <c>Feed(0x01, …)</c>. Any other element — a variable, a spread, a value above
/// 255 — ends the run.
/// </para>
/// <para>
/// <b>What does not.</b> Comments and XML docs are trivia, not literals, so they are never read: a
/// comment cannot be fed to a parser and so cannot become the second fixture R4 forbids. A GUID's text
/// (<c>11111111-2222-…</c>) is decoded as the separate groups it is written in, none of which reaches
/// eight bytes: it is a UUID value a test compares against, not a dump of a frame.
/// </para>
/// <para>
/// <b>A run of one repeated byte proves nothing</b>, so a window of the capture made of a single value
/// is not matched. This was measured, not assumed: the capture's audio payload is near silence, and on
/// the tree as this guard landed the only site the rule found without that exclusion, besides this
/// guard's own zero-run fixtures, was a hand-built frame in <c>AudioSocketProtocolTests</c> whose ten
/// zero bytes coincide with it. Every other window of
/// the capture — its headers, its UUIDs, the <c>ff ff</c> and <c>01</c> samples of its payload — holds at
/// least two values and is matched.
/// </para>
/// </remarks>
internal static class AudioSocketCaptureScanner
{
    /// <summary>The shortest run of captured bytes that counts as a copy.</summary>
    public const int MinimumRun = 8;

    /// <summary>
    /// At a position not preceded by a hex digit: two or more pairs of exactly two hex digits, joined by
    /// whitespace or a dash, colon or comma; or else one unseparated token of an even number of hex
    /// digits.
    /// </summary>
    private static readonly Regex HexRun = new(
        @"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}(?![0-9A-Fa-f])(?:(?:\s+|\s*[-:,]\s*)[0-9A-Fa-f]{2}(?![0-9A-Fa-f]))+|(?:[0-9A-Fa-f]{2})+(?![0-9A-Fa-f]))",
        RegexOptions.CultureInvariant);

    private static readonly Regex NonHex = new("[^0-9A-Fa-f]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every <c>const</c> field of <paramref name="fixtureSource"/> whose value, folded across <c>+</c>,
    /// is a hex dump and nothing else, in declaration order. Prose constants are skipped.
    /// </summary>
    public static IReadOnlyList<CapturedConstant> ReadCapture(string fixtureSource)
    {
        ArgumentNullException.ThrowIfNull(fixtureSource);

        var root = CSharpSyntaxTree.ParseText(fixtureSource).GetRoot();
        var constants = new List<CapturedConstant>();
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.ConstKeyword))
                continue;

            foreach (var variable in field.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not { } value || Fold(value) is not { } text)
                    continue;

                if (DecodeWholeDump(text) is { } bytes)
                    constants.Add(new CapturedConstant(variable.Identifier.Text, bytes));
            }
        }

        return constants;
    }

    /// <summary>Every run of byte values <paramref name="source"/> writes as literals, in source order.</summary>
    public static IReadOnlyList<ByteRun> ExtractByteRuns(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var runs = new List<ByteRun>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ExpressionSyntax expression when IsOutermostString(expression, out var text):
                    AddHexRuns(runs, LineOf(expression), text);
                    break;
                case InterpolatedStringTextSyntax interpolatedText:
                    AddHexRuns(runs, LineOf(interpolatedText), interpolatedText.TextToken.ValueText);
                    break;
                case InitializerExpressionSyntax initializer:
                    AddNumericRuns(runs, initializer.Expressions);
                    break;
                case CollectionExpressionSyntax collection:
                    AddNumericRuns(runs, collection.Elements.Select(element => (element as ExpressionElementSyntax)?.Expression));
                    break;
                case ArgumentListSyntax arguments:
                    AddNumericRuns(runs, arguments.Arguments.Select(argument => argument.Expression));
                    break;
            }
        }

        return runs;
    }

    /// <summary>
    /// One violation per run in <paramref name="source"/> that shares at least <see cref="MinimumRun"/>
    /// consecutive bytes with a constant of <paramref name="capture"/>, reporting the longest such
    /// stretch.
    /// </summary>
    public static IReadOnlyList<AudioSocketCaptureViolation> Scan(
        string source,
        string path,
        IReadOnlyList<CapturedConstant> capture)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(capture);

        var windows = IndexWindows(capture);
        var violations = new List<AudioSocketCaptureViolation>();
        foreach (var run in ExtractByteRuns(source))
        {
            AudioSocketCaptureViolation? longest = null;
            for (var start = 0; start + MinimumRun <= run.Bytes.Length; start++)
            {
                if (!windows.TryGetValue(Convert.ToHexString(run.Bytes, start, MinimumRun), out var hits))
                    continue;

                foreach (var (constant, offset) in hits)
                {
                    var length = SharedLength(run.Bytes, start, capture[constant].Bytes, offset);
                    if (longest is null || length > longest.Length)
                        longest = new AudioSocketCaptureViolation(path, run.Line, capture[constant].Name, offset, length);
                }
            }

            if (longest is not null)
                violations.Add(longest);
        }

        return violations;
    }

    /// <summary>
    /// Every window of <see cref="MinimumRun"/> bytes of every constant, keyed by its hex, except the
    /// windows made of a single repeated byte.
    /// </summary>
    private static Dictionary<string, List<(int Constant, int Offset)>> IndexWindows(IReadOnlyList<CapturedConstant> capture)
    {
        var windows = new Dictionary<string, List<(int Constant, int Offset)>>(StringComparer.Ordinal);
        for (var constant = 0; constant < capture.Count; constant++)
        {
            var bytes = capture[constant].Bytes;
            for (var offset = 0; offset + MinimumRun <= bytes.Length; offset++)
            {
                if (IsOneRepeatedByte(bytes.AsSpan(offset, MinimumRun)))
                    continue;

                var key = Convert.ToHexString(bytes, offset, MinimumRun);
                if (!windows.TryGetValue(key, out var hits))
                    windows[key] = hits = [];

                hits.Add((constant, offset));
            }
        }

        return windows;
    }

    private static bool IsOneRepeatedByte(ReadOnlySpan<byte> window)
    {
        foreach (var value in window)
        {
            if (value != window[0])
                return false;
        }

        return true;
    }

    private static int SharedLength(byte[] run, int runStart, byte[] constant, int constantStart)
    {
        var length = 0;
        while (runStart + length < run.Length
            && constantStart + length < constant.Length
            && run[runStart + length] == constant[constantStart + length])
        {
            length++;
        }

        return length;
    }

    /// <summary>
    /// True for a string literal, a <c>+</c> of foldable strings or a parenthesised one, when it is not
    /// itself an operand of a larger foldable string: the whole string is read once, at the top.
    /// </summary>
    private static bool IsOutermostString(ExpressionSyntax expression, out string text)
    {
        text = string.Empty;
        if (expression is not (LiteralExpressionSyntax or BinaryExpressionSyntax or ParenthesizedExpressionSyntax))
            return false;

        if (Fold(expression) is not { } folded)
            return false;

        if (expression.Parent is BinaryExpressionSyntax or ParenthesizedExpressionSyntax
            && Fold((ExpressionSyntax)expression.Parent) is not null)
        {
            return false;
        }

        text = folded;
        return true;
    }

    /// <summary>The value of a string literal, or of a <c>+</c> of string literals; otherwise null.</summary>
    private static string? Fold(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal
            when literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.IsKind(SyntaxKind.Utf8StringLiteralExpression)
            => literal.Token.ValueText,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
            => Fold(binary.Left) is { } left && Fold(binary.Right) is { } right ? left + right : null,
        ParenthesizedExpressionSyntax parenthesized => Fold(parenthesized.Expression),
        _ => null,
    };

    /// <summary>The bytes of a string that is a hex dump from end to end, or null.</summary>
    private static byte[]? DecodeWholeDump(string text)
    {
        var trimmed = text.Trim();
        var match = HexRun.Match(trimmed);
        return match.Success && match.Index == 0 && match.Length == trimmed.Length ? Decode(match.Value) : null;
    }

    private static void AddHexRuns(List<ByteRun> runs, int line, string text)
    {
        foreach (Match match in HexRun.Matches(text))
            runs.Add(new ByteRun(line, Decode(match.Value)));
    }

    private static byte[] Decode(string hexRun) => Convert.FromHexString(NonHex.Replace(hexRun, string.Empty));

    private static void AddNumericRuns(List<ByteRun> runs, IEnumerable<ExpressionSyntax?> elements)
    {
        var current = new List<byte>();
        var line = 0;
        foreach (var element in elements)
        {
            if (element is not null && ByteValue(element) is { } value)
            {
                if (current.Count == 0)
                    line = LineOf(element);

                current.Add(value);
                continue;
            }

            Flush();
        }

        Flush();

        void Flush()
        {
            if (current.Count > 0)
                runs.Add(new ByteRun(line, [.. current]));

            current.Clear();
        }
    }

    /// <summary>An integer literal from 0 to 255, through any casts and parentheses; otherwise null.</summary>
    private static byte? ByteValue(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression) => literal.Token.Value switch
        {
            int value when value is >= 0 and <= byte.MaxValue => (byte)value,
            uint value when value <= byte.MaxValue => (byte)value,
            long value when value is >= 0 and <= byte.MaxValue => (byte)value,
            ulong value when value <= byte.MaxValue => (byte)value,
            _ => null,
        },
        CastExpressionSyntax cast => ByteValue(cast.Expression),
        ParenthesizedExpressionSyntax parenthesized => ByteValue(parenthesized.Expression),
        _ => null,
    };

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
