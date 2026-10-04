namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "a test never reads state through a synchronous continuation": no C# file under
/// <c>Tests/</c> uses <c>TaskContinuationOptions.ExecuteSynchronously</c>, in any spelling a syntactic
/// scan can see. ZERO-TOLERANCE with no baseline: the tree has no site, so the tree assertion is an
/// empty list, and the detector's liveness rests on its fixtures — a true positive for each spelling it
/// reports and a true negative for each it ignores — plus a floor on the files walked. <c>src/</c> is
/// never scanned (see <see cref="SynchronousContinuationScanner"/>).
/// </summary>
public sealed class SynchronousContinuationGuardTests
{
    // Conservative floor, well below the test tree as this guard landed (~600 files): a locator that
    // broke and walked almost nothing would otherwise pass having judged nothing.
    private const int MinimumScannedFiles = 400;

    [Fact]
    public void Guard_ShouldFindNoSynchronousContinuation_WhenScanningTheTestTree()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;

        var sites = TestTreeSource.EnumerateTestSources()
            .SelectMany(file => SynchronousContinuationScanner.Scan(
                File.ReadAllText(file),
                Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(site => site.Path, StringComparer.Ordinal)
            .ThenBy(site => site.Line)
            .ToList();

        sites.Should().BeEmpty(SynchronousContinuationScanner.BuildFailureMessage(sites));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTheTestTree()
    {
        var count = TestTreeSource.EnumerateTestSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real Tests/ tree; a near-zero count means the locator broke and the " +
            "scan would be a false green");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOptionIsQualified()
    {
        const string source =
            "class C { void M(System.Threading.Tasks.Task t) {\n" +
            "    _ = t.ContinueWith(_ => { }, System.Threading.CancellationToken.None,\n" +
            "        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);\n" +
            "} }";

        var sites = SynchronousContinuationScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Should().Be(new SynchronousContinuationSite(
            "x.cs", 3, "TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);"));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOptionIsFullyQualified()
    {
        const string source =
            "class C { void M(System.Threading.Tasks.Task t) =>\n" +
            "    _ = t.ContinueWith(_ => { }, global::System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously); }";

        var sites = SynchronousContinuationScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOptionIsNamedThroughAnAlias()
    {
        const string source =
            "using TCO = System.Threading.Tasks.TaskContinuationOptions;\n" +
            "class C { void M(System.Threading.Tasks.Task t) =>\n" +
            "    _ = t.ContinueWith(_ => { }, TCO.ExecuteSynchronously); }";

        var sites = SynchronousContinuationScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOptionIsBareUnderUsingStatic()
    {
        const string source =
            "using static System.Threading.Tasks.TaskContinuationOptions;\n" +
            "class C { void M(System.Threading.Tasks.Task t) =>\n" +
            "    _ = t.ContinueWith(_ => { }, ExecuteSynchronously); }";

        var sites = SynchronousContinuationScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOptionIsCombinedWithOtherFlags()
    {
        const string source =
            "class C { void M(System.Threading.Tasks.Task t) =>\n" +
            "    _ = t.ContinueWith(_ => { },\n" +
            "        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously); }";

        var sites = SynchronousContinuationScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheOptionIsNamedInAComment()
    {
        const string source =
            "class C {\n" +
            "    // ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously) is not used here.\n" +
            "    /* TaskContinuationOptions.ExecuteSynchronously */\n" +
            "    void M() { }\n" +
            "}";

        SynchronousContinuationScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheOptionIsNamedInAnXmlDoc()
    {
        const string source =
            "class C {\n" +
            "    /// <summary>Never uses <c>TaskContinuationOptions.ExecuteSynchronously</c>.</summary>\n" +
            "    void M() { }\n" +
            "}";

        SynchronousContinuationScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheOptionIsNamedInAStringLiteral()
    {
        const string source =
            "class C { string M() => \"TaskContinuationOptions.ExecuteSynchronously\" + $\"ExecuteSynchronously{1}\"; }";

        SynchronousContinuationScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenASourceRunsItsContinuationsAsynchronously()
    {
        const string source =
            "class C { System.Threading.Tasks.TaskCompletionSource M() =>\n" +
            "    new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously); }";

        SynchronousContinuationScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void BuildFailureMessage_ShouldNameEachSite_WhenThereAreSites()
    {
        var message = SynchronousContinuationScanner.BuildFailureMessage(
        [
            new SynchronousContinuationSite("Tests/A.cs", 12, "TaskContinuationOptions.ExecuteSynchronously,"),
            new SynchronousContinuationSite("Tests/B.cs", 3, "ExecuteSynchronously);"),
        ]);

        message.Should().Contain("2 test source line(s)")
            .And.Contain("Tests/A.cs:12: TaskContinuationOptions.ExecuteSynchronously,")
            .And.Contain("Tests/B.cs:3: ExecuteSynchronously);");
    }
}
