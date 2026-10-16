using System.Text.RegularExpressions;
using Verbara.Sdk.Ami.SourceGenerators;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The event registry and the event deserializer suppress an obsolete diagnostic only for an obsolete symbol they bind:
/// one <c>#pragma warning disable</c> per distinct diagnostic (<c>CS0618</c>, or the <c>DiagnosticId</c> of an
/// <c>[Obsolete]</c>), and none when they bind no obsolete symbol.
/// </summary>
/// <remarks>
/// A blanket <c>CS0618</c> hides every obsolete type the generated code binds, so it outlives the types it was written
/// for, and it does not cover an <c>[Obsolete]</c> that carries a <c>DiagnosticId</c>: such a mark breaks the SDK's own
/// build under TreatWarningsAsErrors in the generated file. Each case runs both generators over a synthetic compilation
/// (<see cref="GeneratorHarness"/>), reads the pragmas of each generated file and compiles the result.
/// </remarks>
public sealed class ObsoletePragmaEmissionTests
{
    private const string Registry = nameof(EventRegistryGenerator);
    private const string Deserializer = nameof(EventDeserializerGenerator);
    private const string ProbeId = "PROBE0001";

    /// <summary>What a bare <c>#pragma warning disable</c>, with no id, reads as: it silences every diagnostic.</summary>
    private const string Bare = "(bare: every diagnostic)";

    /// <summary>The ids that silence an obsolete diagnostic: the two compiler ones and the probe's <c>DiagnosticId</c>.</summary>
    private static readonly HashSet<string> ObsoleteIds = new(StringComparer.Ordinal) { "CS0612", "CS0618", ProbeId, Bare };

    private const string NoObsoleteSymbol = """
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartEvent : ManagerEvent
        {
            public string? Channel { get; set; }
        }
        """;

    private const string ObsoleteType = """
        using System;
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartEvent : ManagerEvent
        {
            public string? Channel { get; set; }
        }

        [Obsolete("The module that sent it is gone."__ID__)]
        [VerbaraMapping("ProbeGone")]
        public sealed class ProbeGoneEvent : ManagerEvent
        {
            public string? Channel { get; set; }
        }
        """;

    private const string ObsoleteMember = """
        using System;
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartEvent : ManagerEvent
        {
            public string? Channel { get; set; }

            [Obsolete("No supported version sends this header."__ID__)]
            public string? Gone { get; set; }
        }
        """;

    private static readonly Regex DisablePragma = new(
        @"^[ \t]*#[ \t]*pragma[ \t]+warning[ \t]+disable\b(?<ids>[^/\r\n]*)", RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    [Fact]
    public void Generators_ShouldEmitNoObsoleteSuppression_WhenNoBoundSymbolIsObsolete()
    {
        var run = GeneratorHarness.Run(NoObsoleteSymbol, new EventRegistryGenerator(), new EventDeserializerGenerator());

        using (new FluentAssertions.Execution.AssertionScope())
        {
            DisabledIds(run, Registry).Should().BeEmpty(
                "the registry binds no obsolete type, so it has nothing to suppress");
            DisabledIds(run, Deserializer).Should().BeEmpty(
                "the deserializer binds no obsolete type or member, so it has nothing to suppress");
            run.WarningsAndErrors.Should().BeEmpty("the compilation builds clean without any suppression");
        }
    }

    [Theory]
    [InlineData(false, "CS0618")]
    [InlineData(true, ProbeId)]
    public void Generators_ShouldSuppressExactlyTheTypesDiagnostic_WhenABoundEventTypeIsObsolete(bool withId, string expected)
    {
        var run = GeneratorHarness.Run(Mark(ObsoleteType, withId), new EventRegistryGenerator(), new EventDeserializerGenerator());

        using (new FluentAssertions.Execution.AssertionScope())
        {
            DisabledIds(run, Registry).Should().Equal([expected],
                "the registry binds the obsolete type in its factory and suppresses exactly its diagnostic");
            DisabledIds(run, Deserializer).Should().Equal([expected],
                "the deserializer binds the obsolete type in its switch and suppresses exactly its diagnostic");
            run.WarningsAndErrors.Should().BeEmpty("the one obsolete type is suppressed where it is bound, and nowhere else warns");
        }
    }

    [Theory]
    [InlineData(false, "CS0618")]
    [InlineData(true, ProbeId)]
    public void Deserializer_ShouldSuppressExactlyTheMembersDiagnostic_WhenABoundEventMemberIsObsolete(bool withId, string expected)
    {
        var run = GeneratorHarness.Run(Mark(ObsoleteMember, withId), new EventRegistryGenerator(), new EventDeserializerGenerator());

        using (new FluentAssertions.Execution.AssertionScope())
        {
            DisabledIds(run, Deserializer).Should().Equal([expected],
                "the deserializer assigns the obsolete member and suppresses exactly its diagnostic");
            DisabledIds(run, Registry).Should().BeEmpty(
                "the registry binds the type, which is not obsolete, and never the member");
            run.WarningsAndErrors.Should().BeEmpty("the one obsolete member is suppressed where it is bound, and nowhere else warns");
        }
    }

    [Fact]
    public void DisabledIds_ShouldReadEveryObsoleteIdOfEveryPragma_WhenAFileCarriesSeveral()
    {
        // The reader's control: two pragmas, one listing two ids and a trailing comment; a non-obsolete id is not read.
        var text = "#nullable enable\n#pragma warning disable CS0618 // why\n  #pragma warning disable PROBE0001, CS0612\n"
            + "#pragma warning disable CS1591 // a doc comment, not an obsolete diagnostic\n#pragma warning restore CS0618\n"
            + "#pragma warning disable 612\n";

        ReadDisabledIds(text).Should().Equal(["CS0612", "CS0618", "PROBE0001"]);
    }

    [Theory]
    [InlineData("#pragma warning disable\n")]
    [InlineData("#pragma warning disable // everything\n")]
    [InlineData("  #pragma warning disable   \r\n")]
    public void DisabledIds_ShouldReadABareDisableAsAnObsoleteSuppression_WhenItNamesNoId(string text)
    {
        // A bare disable silences CS0618 along with everything else, so it counts as an obsolete suppression.
        ReadDisabledIds(text).Should().Equal([Bare]);
    }

    private static string Mark(string source, bool withId) =>
        source.Replace("__ID__", withId ? $", DiagnosticId = \"{ProbeId}\"" : "", StringComparison.Ordinal);

    private static IReadOnlyList<string> DisabledIds(GeneratorRun run, string generator) =>
        ReadDisabledIds(run.Single(generator).Text);

    /// <summary>A bare number is the compiler's own id: <c>618</c> disables <c>CS0618</c>.</summary>
    private static string Normalize(string id) =>
        id.Length > 0 && id.All(char.IsAsciiDigit) ? "CS" + id.PadLeft(4, '0') : id;

    /// <summary>
    /// The obsolete-diagnostic ids every <c>#pragma warning disable</c> of <paramref name="text"/> names, plus
    /// <see cref="Bare"/> for one that names no id. Ids of other diagnostics are not this requirement's concern.
    /// </summary>
    private static IReadOnlyList<string> ReadDisabledIds(string text) =>
    [
        .. DisablePragma.Matches(text)
            .SelectMany(static m => m.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } ids
                ? ids
                : [Bare])
            .Select(Normalize)
            .Where(ObsoleteIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];
}
