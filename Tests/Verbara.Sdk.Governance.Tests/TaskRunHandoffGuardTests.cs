using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for ADR-0058 R2 (openspec change <c>a-decision-is-held-by-a-test-that-can-fail</c>,
/// spec requirement "A hand-off to Task.Run passes CancellationToken.None"): no discarded
/// <c>Task.Run</c> in <c>src/</c> passes a token other than <c>CancellationToken.None</c>. It fired on
/// two sites when it landed, both owned by a change that fixes them, so they are listed in
/// <c>decision-guards-baseline.json</c> and the tree must match that list exactly: a new site fails,
/// and so does an entry whose site was fixed and left behind. Carries two liveness floors — the files
/// walked and the discarded <c>Task.Run</c> calls recognised — and detector fixtures for every shape
/// the scanner claims to see and every shape it must pass.
/// </summary>
public sealed class TaskRunHandoffGuardTests
{
    // Conservative floors, well below the tree as this guard landed (~870 src files; 7 discarded
    // Task.Run calls, two of them the baselined sites). The second floor is what keeps the guard
    // honest once those two are fixed and the baseline is empty: a detector that stopped recognising
    // the hand-off shape would then pass having inspected nothing.
    private const int MinimumScannedFiles = 400;
    private const int MinimumDiscardedTaskRuns = 4;

    [Fact]
    public void Guard_ShouldMatchTheBaselineExactly_WhenScanningTheSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

        var sites = new List<DecisionGuardSite>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
            sites.AddRange(TaskRunHandoffScanner.Scan(File.ReadAllText(file), ToRelative(repoRoot, file)));

        var match = DecisionGuardBaseline.LoadCommitted().Match(TaskRunHandoffScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(BuildFailureMessage(match));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTheSrcTree()
    {
        var count = SrcTreeSource.EnumerateSrcSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real src tree; a near-zero count means the locator broke and " +
            "the hand-off scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldRecogniseDiscardedTaskRuns_WhenWalkingTheSrcTree()
    {
        var discarded = SrcTreeSource.EnumerateSrcSources()
            .Sum(file => TaskRunHandoffScanner.CountDiscardedTaskRuns(File.ReadAllText(file)));

        discarded.Should().BeGreaterThanOrEqualTo(
            MinimumDiscardedTaskRuns,
            "the detector must recognise the fire-and-forget hand-offs src/ actually writes; finding " +
            "almost none means the idiom moved or the discard detection broke, and the guard now scans " +
            "for a shape that no longer exists");
    }

    [Fact]
    public void GuardName_ShouldBeRegistered_WhenTheBaselineIsRead()
    {
        DecisionGuardBaseline.RegisteredGuards.Should().Contain(
            TaskRunHandoffScanner.GuardName,
            "an unregistered guard's baseline entries are reported as orphans, and a registration that " +
            "drifts from the scanner's name would make every entry of this guard one");
    }

    [Fact]
    public void Match_ShouldNameTheSite_WhenAFixtureSiteIsNotInTheBaseline()
    {
        const string source =
            "class Server {\n" +
            "    void Start(System.Threading.CancellationToken ct) {\n" +
            "        _ = Task.Run(() => AcceptLoopAsync(ct), ct);\n" +
            "    }\n" +
            "}";
        var sites = TaskRunHandoffScanner.Scan(source, "src/Fixture/Server.cs");

        var match = DecisionGuardBaseline.Parse("""{ "entries": [] }""").Match(TaskRunHandoffScanner.GuardName, sites);

        match.IsExact.Should().BeFalse();
        match.Describe().Should().Contain("src/Fixture/Server.cs:3")
            .And.Contain("Task.Run(() => AcceptLoopAsync(ct), ct)");
    }

    [Fact]
    public void Match_ShouldBeExact_WhenTheBaselineListsTheFixtureSiteAsItIsQuoted()
    {
        // The key is pasted from the failure message into the entry, so the scanner's key and the
        // matcher's normalization must agree on a site written across several lines.
        const string source =
            "class Server {\n" +
            "    void Start(System.Threading.CancellationToken ct) {\n" +
            "        _ = Task.Run(\n" +
            "            () => AcceptLoopAsync(ct),\n" +
            "            ct);\n" +
            "    }\n" +
            "}";
        const string baseline = """
            { "entries": [
              { "guard": "task-run-handoff-token", "path": "src/Fixture/Server.cs",
                "key": "Task.Run( () => AcceptLoopAsync(ct), ct)", "reason": "r", "owner": "o" }
            ] }
            """;
        var sites = TaskRunHandoffScanner.Scan(source, "src/Fixture/Server.cs");

        var match = DecisionGuardBaseline.Parse(baseline).Match(TaskRunHandoffScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(match.Describe());
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAPositionalTokenIsPassed()
    {
        const string source =
            "class C {\n" +
            "    void M(System.Threading.CancellationToken ct) {\n" +
            "        _ = Task.Run(() => Work(ct), ct);\n" +
            "    }\n" +
            "}";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Should().Be(new DecisionGuardSite("x.cs", 3, "Task.Run(() => Work(ct), ct)"));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheTokenIsPassedByName()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken stoppingToken) {\n" +
            "    _ = Task.Run(() => Work(stoppingToken), cancellationToken: stoppingToken);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("Task.Run(() => Work(stoppingToken), cancellationToken: stoppingToken)");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheNamedTokenComesFirst()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    _ = Task.Run(cancellationToken: ct, function: () => Work(ct));\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheWorkIsABlockBodiedAsyncLambda()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    _ = Task.Run(async () => { await Work(ct); }, ct);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheWorkIsAMethodGroup()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    _ = Task.Run(Loop, ct);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("Task.Run(Loop, ct)");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheDiscardIsTheBodyOfAnExpressionBodiedMember()
    {
        const string source =
            "class C {\n" +
            "    System.Threading.CancellationToken _ct;\n" +
            "    void Start() => _ = Task.Run(() => Loop(_ct), _ct);\n" +
            "}";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheDiscardIsTheBodyOfAnExpressionBodiedLambda()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    System.Action start = () => _ = Task.Run(() => Loop(ct), ct);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("Task.Run(() => Loop(ct), ct)");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheCallIsABareStatement()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    Task.Run(() => Loop(ct), ct);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAVoidMethodsExpressionBodyIsTheCall()
    {
        const string source =
            "class C {\n" +
            "    System.Threading.CancellationToken _ct;\n" +
            "    void Start() => Task.Run(() => Loop(_ct), _ct);\n" +
            "}";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheResultIsAssignedToADiscardLocal()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    var _ = Task.Run(() => Loop(ct), ct);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheDiscardLooksThroughParenthesesAndConfigureAwait()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    _ = (Task.Run(() => Loop(ct), ct)).ConfigureAwait(false);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheCallIsFullyQualified()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    _ = global::System.Threading.Tasks.Task.Run<int>(() => 1, ct);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Theory]
    [InlineData("default")]
    [InlineData("default(System.Threading.CancellationToken)")]
    [InlineData("new System.Threading.CancellationToken()")]
    [InlineData("_cts.Token")]
    public void Scan_ShouldFlag_WhenTheTokenIsNotWrittenAsCancellationTokenNone(string token)
    {
        var source =
            "class C { System.Threading.CancellationTokenSource _cts = new(); void M() {\n" +
            $"    _ = Task.Run(() => Loop(), {token});\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Theory]
    [InlineData("CancellationToken.None")]
    [InlineData("System.Threading.CancellationToken.None")]
    [InlineData("global::System.Threading.CancellationToken.None")]
    [InlineData("(CancellationToken.None)")]
    [InlineData("cancellationToken: CancellationToken.None")]
    public void Scan_ShouldPass_WhenTheTokenIsCancellationTokenNone(string token)
    {
        var source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            $"    _ = Task.Run(() => HandleConnectionAsync(client, ct), {token});\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        TaskRunHandoffScanner.CountDiscardedTaskRuns(source).Should().Be(1, "the call is a discarded hand-off the scanner inspected");
    }

    [Fact]
    public void Scan_ShouldPass_WhenNoTokenIsPassed()
    {
        // The one-argument overloads run under CancellationToken.None; a token the work captures is
        // the work's own business, not the hand-off's.
        const string source =
            "class C { System.Threading.CancellationTokenSource _cts = new();\n" +
            "    internal void StartReadLoop() =>\n" +
            "        _ = Task.Run(() => ReadLoopAsync(_cts.Token));\n" +
            "}";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        TaskRunHandoffScanner.CountDiscardedTaskRuns(source).Should().Be(1);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheTaskIsAwaited()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M(System.Threading.CancellationToken ct) {\n" +
            "    await Task.Run(() => Work(ct), ct);\n" +
            "    await Task.Run(() => Work(ct), ct).ConfigureAwait(false);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        TaskRunHandoffScanner.CountDiscardedTaskRuns(source).Should().Be(0);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheTaskIsStoredReturnedOrPassedOn()
    {
        const string source =
            "class C {\n" +
            "    System.Threading.Tasks.Task _loop;\n" +
            "    System.Threading.Tasks.Task M(System.Threading.CancellationToken ct) {\n" +
            "        _loop = Task.Run(() => Loop(ct), ct);\n" +
            "        var receiveTask = Task.Run(async () => await Receive(ct), ct);\n" +
            "        Track(Task.Run(() => Loop(ct), ct));\n" +
            "        return Task.Run(() => Loop(ct), ct);\n" +
            "    }\n" +
            "    System.Threading.Tasks.Task Start(System.Threading.CancellationToken ct) => Task.Run(() => Loop(ct), ct);\n" +
            "    System.Func<System.Threading.Tasks.Task> Starter(System.Threading.CancellationToken ct) => () => Task.Run(() => Loop(ct), ct);\n" +
            "}";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        TaskRunHandoffScanner.CountDiscardedTaskRuns(source).Should().Be(0);
    }

    [Fact]
    public void Scan_ShouldPass_WhenARunMethodIsNotTaskRun()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    _ = _runner.Run(() => Loop(ct), ct);\n" +
            "    _ = Pipeline.Run(ct, ct);\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheCallIsOnlyMentionedInCommentsOrStrings()
    {
        const string source =
            "/// <c>_ = Task.Run(() => Work(ct), ct);</c> is the defect.\n" +
            "class C { void M() {\n" +
            "    // _ = Task.Run(() => Work(ct), ct);\n" +
            "    var s = \"_ = Task.Run(() => Work(ct), ct);\";\n" +
            "} }";

        var sites = TaskRunHandoffScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(DecisionGuardMatch match)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "ADR-0058 R2: a discarded Task.Run passes CancellationToken.None as its own token, written " +
            "explicitly. Task.Run's token cancels the work item if the pool has not started it yet, so a " +
            "live token can drop a hand-off that carries the only reference to a resource the caller " +
            "already holds, and nobody observes the cancelled task.");
        sb.AppendLine(
            "Pass the token INTO the work (() => Work(ct)) and CancellationToken.None to Task.Run itself.");
        sb.Append(match.Describe());
        return sb.ToString();
    }
}
