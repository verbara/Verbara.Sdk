using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process architecture guard (ADR-0014 §2 G4 / ADR-0022): parses every product source file with
/// Roslyn and fails the build if any file uses an AOT-breaking reflection API without a valid inline
/// <c>// aot-allow:</c> marker. Unlike the sync-fence ratchet, this guard is ZERO-TOLERANCE — the
/// Sdk <c>src/</c> tree carries no banned reflection today and must never gain any (an AOT-existential
/// invariant: the shippable image must be Native AOT). Includes a liveness self-test (the scan must
/// actually walk a large file set) and detector unit tests that pin both true positives, the AOT-safe
/// forms that must NOT flag, and the prose/string false-positive immunity.
/// </summary>
public sealed class ReflectionBanGuardTests
{
    // Conservative floor: a floor well below the real src-file count (~863) defeats the "found zero
    // files -> false green" failure mode while tolerating churn.
    private const int MinimumScannedFiles = 400;

    [Fact]
    public void Guard_ShouldFindNoBannedReflection_InSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

        var violations = new List<ReflectionViolation>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            violations.AddRange(ReflectionBanScanner.Scan(source, relative));
        }

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingSrcTree()
    {
        var count = SrcTreeSource.EnumerateSrcSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real src tree; a near-zero count means the locator broke and " +
            "the reflection scan would be a false green");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenActivatorCreateInstanceNonGeneric()
    {
        const string source = "class C { void M() { var x = System.Activator.CreateInstance(typeof(C)); } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Activator.CreateInstance");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenStaticTypeGetType()
    {
        const string source = "class C { void M() { var t = Type.GetType(\"N.C\"); } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Type.GetType");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMakeGenericType()
    {
        const string source = "class C { void M(System.Type t) { var g = t.MakeGenericType(typeof(int)); } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Type.MakeGenericType");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMakeGenericMethod()
    {
        const string source = "class C { void M(System.Reflection.MethodInfo mi) { var g = mi.MakeGenericMethod(typeof(int)); } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("MethodInfo.MakeGenericMethod");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenNewDynamicMethod()
    {
        const string source =
            "class C { void M() { var d = new DynamicMethod(\"m\", null, null); } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Reflection.Emit.DynamicMethod");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenUsingReflectionEmit()
    {
        const string source =
            "using System.Reflection.Emit;\n" +
            "class C { void M() { } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("using System.Reflection.Emit");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenActivatorCreateInstanceGeneric()
    {
        const string source = "class C { void M() { var x = System.Activator.CreateInstance<C>(); } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenInstanceGetType()
    {
        const string source = "class C { void M(object obj) { var t = obj.GetType(); } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenBannedApiInComment()
    {
        const string source = "class C { void M() { /* avoid Type.GetType and Activator.CreateInstance here */ } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenBannedApiInXmlDoc()
    {
        const string source =
            "/// <c>Activator.CreateInstance</c> is banned; see <c>Type.GetType</c>.\n" +
            "class C { void M() { } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenBannedApiInStringLiteral()
    {
        const string source = "class C { void M() { var s = \"call Type.GetType( now\"; } }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenWellFormedMarkerOnSameLine()
    {
        const string source =
            "class C { void M() {\n" +
            "    var t = Type.GetType(\"N.C\"); // aot-allow: VERIFIED-SAFE — name is a compile-time const\n" +
            "} }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenMarkerOnPrecedingLine()
    {
        const string source =
            "class C { void M() {\n" +
            "    // aot-allow: SOURCE-GEN — resolved by the source generator\n" +
            "    var t = Type.GetType(\"N.C\");\n" +
            "} }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenBareMarkerHasNoCategory()
    {
        const string source =
            "class C { void M() {\n" +
            "    var t = Type.GetType(\"N.C\"); // aot-allow:\n" +
            "} }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Type.GetType");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerCategoryUnknown()
    {
        const string source =
            "class C { void M() {\n" +
            "    var t = Type.GetType(\"N.C\"); // aot-allow: WHATEVER — y\n" +
            "} }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Type.GetType");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerHasNoReason()
    {
        const string source =
            "class C { void M() {\n" +
            "    var t = Type.GetType(\"N.C\"); // aot-allow: SOURCE-GEN\n" +
            "} }";

        var violations = ReflectionBanScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Type.GetType");
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<ReflectionViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" AOT-breaking reflection site(s) found in the src/ tree (ADR-0022 — the shippable image must be Native AOT):");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  [").Append(v.Api).Append("]  ").AppendLine(v.Detail);
        }

        sb.AppendLine(
            "Remove the reflection (prefer source generators / static generics) or, if the site is " +
            "genuinely AOT-safe, annotate it with // aot-allow: CATEGORY — reason " +
            "(CATEGORY ∈ SOURCE-GEN | INTEROP | VERIFIED-SAFE).");
        return sb.ToString();
    }
}
