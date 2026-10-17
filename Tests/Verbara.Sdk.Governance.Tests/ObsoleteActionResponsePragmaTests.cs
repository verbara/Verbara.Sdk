using System.Text.RegularExpressions;
using Verbara.Sdk.Ami.SourceGenerators;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The action serializer and the response deserializer suppress an obsolete diagnostic only for an obsolete type they
/// bind: one <c>#pragma warning disable</c> per distinct diagnostic (<c>CS0618</c>, or the <c>DiagnosticId</c> of an
/// <c>[Obsolete]</c>), and none when they bind no obsolete type. The last two cases check that the two event generators
/// do the same for an event type marked with the deprecation id the SDK's dead-module marks carry.
/// </summary>
/// <remarks>
/// Each generated file names every mapped type it binds (the serializer's <c>switch</c> arms, the deserializer's
/// factory registry), so an <c>[Obsolete]</c> on one of them fails the SDK's own build under TreatWarningsAsErrors in
/// the generated file unless that file suppresses the diagnostic. Each case runs the generator over a synthetic
/// compilation (<see cref="GeneratorHarness"/>), reads the pragmas of the generated file and compiles the result.
/// </remarks>
public sealed class ObsoleteActionResponsePragmaTests
{
    private const string Serializer = nameof(ActionSerializerGenerator);
    private const string Responses = nameof(ResponseDeserializerGenerator);
    private const string Registry = nameof(EventRegistryGenerator);
    private const string Deserializer = nameof(EventDeserializerGenerator);
    private const string DeprecationId = "VSDK0005";

    /// <summary>What a bare <c>#pragma warning disable</c>, with no id, reads as: it silences every diagnostic.</summary>
    private const string Bare = "(bare: every diagnostic)";

    /// <summary>The ids that silence an obsolete diagnostic: the two compiler ones and the deprecation id.</summary>
    private static readonly HashSet<string> ObsoleteIds = new(StringComparer.Ordinal) { "CS0612", "CS0618", DeprecationId, Bare };

    private const string Actions = """
        using System;
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartAction : ManagerAction
        {
            public string? Channel { get; set; }
        }

        __MARK__
        [VerbaraMapping("ProbeGone")]
        public sealed class ProbeGoneAction : ManagerAction
        {
            public string? Channel { get; set; }
        }
        """;

    private const string ResponseTypes = """
        using System;
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartResponse : ManagerResponse
        {
            public string? Channel { get; set; }
        }

        __MARK__
        [VerbaraMapping("ProbeGone")]
        public sealed class ProbeGoneResponse : ManagerResponse
        {
            public string? Channel { get; set; }
        }
        """;

    private const string EventTypes = """
        using System;
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartEvent : ManagerEvent
        {
            public string? Channel { get; set; }
        }

        [Obsolete("Asterisk 12 renamed this event.", DiagnosticId = "VSDK0005", UrlFormat = "https://example.invalid/guide#vsdk0005")]
        [VerbaraMapping("ProbeGone")]
        public sealed class ProbeGoneEvent : ManagerEvent
        {
            public string? Channel { get; set; }
        }
        """;

    private static readonly Regex DisablePragma = new(
        @"^[ \t]*#[ \t]*pragma[ \t]+warning[ \t]+disable\b(?<ids>[^/\r\n]*)", RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData(Serializer)]
    [InlineData(Responses)]
    public void Generator_ShouldEmitNoObsoleteSuppression_WhenNoBoundTypeIsObsolete(string generator)
    {
        var run = RunOne(generator, Mark(SourceFor(generator), null));

        using (new FluentAssertions.Execution.AssertionScope())
        {
            DisabledIds(run, generator).Should().BeEmpty("the generated file binds no obsolete type, so it has nothing to suppress");
            run.WarningsAndErrors.Should().BeEmpty("the compilation builds clean without any suppression");
        }
    }

    [Theory]
    [InlineData(Serializer, false, "CS0618")]
    [InlineData(Serializer, true, DeprecationId)]
    [InlineData(Responses, false, "CS0618")]
    [InlineData(Responses, true, DeprecationId)]
    public void Generator_ShouldSuppressExactlyTheTypesDiagnostic_WhenABoundTypeIsObsolete(string generator, bool withId, string expected)
    {
        var run = RunOne(generator, Mark(SourceFor(generator), withId));

        using (new FluentAssertions.Execution.AssertionScope())
        {
            DisabledIds(run, generator).Should().Equal([expected],
                "the generated file binds the obsolete type and suppresses exactly its diagnostic");
            run.WarningsAndErrors.Should().BeEmpty("the one obsolete type is suppressed where it is bound, and nowhere else warns");
        }
    }

    [Theory]
    [InlineData(Registry)]
    [InlineData(Deserializer)]
    public void EventGenerator_ShouldSuppressTheDeprecationId_WhenABoundEventTypeCarriesIt(string generator)
    {
        var run = GeneratorHarness.Run(EventTypes, new EventRegistryGenerator(), new EventDeserializerGenerator());

        using (new FluentAssertions.Execution.AssertionScope())
        {
            DisabledIds(run, generator).Should().Equal([DeprecationId],
                "the generated file binds the event type marked with the deprecation id, on the type itself");
            run.WarningsAndErrors.Should().BeEmpty("the marked event type is suppressed where it is bound, and nowhere else warns");
        }
    }

    private static string SourceFor(string generator) => generator == Serializer ? Actions : ResponseTypes;

    private static GeneratorRun RunOne(string generator, string source) => generator == Serializer
        ? GeneratorHarness.Run(source, new ActionSerializerGenerator())
        : GeneratorHarness.Run(source, new ResponseDeserializerGenerator());

    /// <summary>No mark (<see langword="null"/>), a plain <c>[Obsolete("…")]</c>, or one carrying the deprecation id.</summary>
    private static string Mark(string source, bool? withId) => source.Replace(
        "__MARK__",
        withId is not { } carriesId
            ? ""
            : carriesId
                ? $"[Obsolete(\"The module that served it is gone.\", DiagnosticId = \"{DeprecationId}\")]"
                : "[Obsolete(\"The module that served it is gone.\")]",
        StringComparison.Ordinal);

    /// <summary>A bare number is the compiler's own id: <c>618</c> disables <c>CS0618</c>.</summary>
    private static string Normalize(string id) =>
        id.Length > 0 && id.All(char.IsAsciiDigit) ? "CS" + id.PadLeft(4, '0') : id;

    /// <summary>The obsolete-diagnostic ids every <c>#pragma warning disable</c> of the generator's file names.</summary>
    private static IReadOnlyList<string> DisabledIds(GeneratorRun run, string generator) =>
    [
        .. DisablePragma.Matches(run.Single(generator).Text)
            .SelectMany(static m => m.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } ids
                ? ids
                : [Bare])
            .Select(Normalize)
            .Where(ObsoleteIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];
}
