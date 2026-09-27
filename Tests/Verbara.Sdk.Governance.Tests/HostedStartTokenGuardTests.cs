using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for ADR-0059 R2 (openspec change <c>a-decision-is-held-by-a-test-that-can-fail</c>,
/// spec requirement "A hosted service does not keep its start token"): no hosted service in
/// <c>src/</c> assigns its <c>StartAsync</c> token to a field, or a linked source over it to a field.
/// It fired on two sites when it landed, each owned by a change or a ruling that decides it, so they
/// are listed in <c>decision-guards-baseline.json</c> and the tree must match that list exactly: a new
/// site fails, and so does an entry whose site was fixed and left behind. Carries two liveness floors
/// — the files walked and the hosted-service <c>StartAsync</c> methods inspected — and detector
/// fixtures for every shape the scanner claims to report and every shape it must pass.
/// </summary>
public sealed class HostedStartTokenGuardTests
{
    // Conservative floors, well below the tree as this guard landed (~865 src files; 10 hosted-service
    // StartAsync methods taking a token, two of them the baselined sites). The second floor is what
    // keeps the guard honest once those two are fixed and the baseline is empty: a detector that
    // stopped recognising hosted services would then pass having inspected nothing.
    private const int MinimumScannedFiles = 400;
    private const int MinimumHostedStartMethods = 6;

    [Fact]
    public void Guard_ShouldMatchTheBaselineExactly_WhenScanningTheSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

        var sites = new List<DecisionGuardSite>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
            sites.AddRange(HostedStartTokenScanner.Scan(File.ReadAllText(file), ToRelative(repoRoot, file)));

        var match = DecisionGuardBaseline.LoadCommitted().Match(HostedStartTokenScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(BuildFailureMessage(match));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTheSrcTree()
    {
        var count = SrcTreeSource.EnumerateSrcSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real src tree; a near-zero count means the locator broke and " +
            "the start-token scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldInspectManyHostedStartMethods_WhenWalkingTheSrcTree()
    {
        var methods = SrcTreeSource.EnumerateSrcSources()
            .Sum(file => HostedStartTokenScanner.CountHostedStartMethods(File.ReadAllText(file)));

        methods.Should().BeGreaterThanOrEqualTo(
            MinimumHostedStartMethods,
            "the detector must see the hosted services src/ actually registers; finding almost none " +
            "means the hosted-type or StartAsync detection broke, and an empty baseline would then pass " +
            "having inspected nothing");
    }

    [Fact]
    public void GuardName_ShouldBeRegistered_WhenTheBaselineIsRead()
    {
        DecisionGuardBaseline.RegisteredGuards.Should().Contain(
            HostedStartTokenScanner.GuardName,
            "an unregistered guard's baseline entries are reported as orphans, and a registration that " +
            "drifts from the scanner's name would make every entry of this guard one");
    }

    [Fact]
    public void Match_ShouldNameTheSite_WhenAFixtureSiteIsNotInTheBaseline()
    {
        const string source =
            "class Broker : IHostedService {\n" +
            "    private CancellationToken _stoppingToken;\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        _stoppingToken = cancellationToken;\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";
        var sites = HostedStartTokenScanner.Scan(source, "src/Fixture/Broker.cs");

        var match = DecisionGuardBaseline.Parse("""{ "entries": [] }""").Match(HostedStartTokenScanner.GuardName, sites);

        match.IsExact.Should().BeFalse();
        match.Describe().Should().Contain("src/Fixture/Broker.cs:4")
            .And.Contain("_stoppingToken = cancellationToken");
    }

    [Fact]
    public void Match_ShouldBeExact_WhenTheBaselineListsAMultiLineSiteAsItIsQuoted()
    {
        // The key is pasted from the failure message into the entry, so the scanner's key and the
        // matcher's normalization must agree on a site written across several lines.
        const string source =
            "class Sweeper : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        _cts = CancellationTokenSource\n" +
            "            .CreateLinkedTokenSource(cancellationToken);\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";
        const string baseline = """
            { "entries": [
              { "guard": "hosted-service-start-token", "path": "src/Fixture/Sweeper.cs",
                "key": "_cts = CancellationTokenSource .CreateLinkedTokenSource(cancellationToken)", "reason": "r", "owner": "o" }
            ] }
            """;
        var sites = HostedStartTokenScanner.Scan(source, "src/Fixture/Sweeper.cs");

        var match = DecisionGuardBaseline.Parse(baseline).Match(HostedStartTokenScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(match.Describe());
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheStartTokenIsAssignedToAField()
    {
        const string source =
            "class Broker : IHostedService {\n" +
            "    private CancellationToken _stoppingToken;\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        _stoppingToken = cancellationToken;\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Should().Be(new DecisionGuardSite("x.cs", 4, "_stoppingToken = cancellationToken"));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAFieldIsAssignedALinkedSourceOverTheStartToken()
    {
        const string source =
            "class Sweeper : IHostedService {\n" +
            "    private CancellationTokenSource? _cts;\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Should().Be(
            new DecisionGuardSite("x.cs", 4, "_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)"));
    }

    [Theory]
    [InlineData("_cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.ApplicationStopping)")]
    [InlineData("_cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping, ct)")]
    [InlineData("_cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct)")]
    [InlineData("_cts = CancellationTokenSource.CreateLinkedTokenSource(new[] { ct, _other })")]
    [InlineData("_cts ??= CancellationTokenSource.CreateLinkedTokenSource(ct)")]
    [InlineData("_token = CancellationTokenSource.CreateLinkedTokenSource(ct).Token")]
    [InlineData("this._token = ct")]
    [InlineData("_holder.Token = ct")]
    [InlineData("Current = ct")]
    [InlineData("_tokens[0] = ct")]
    [InlineData("_token = (ct)")]
    [InlineData("_token = _restart ? ct : CancellationToken.None")]
    public void Scan_ShouldFlag_WhenTheStartTokenOrALinkOverItIsKept(string assignment)
    {
        var source =
            "class Service : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken ct) {\n" +
            $"        {assignment};\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be(assignment);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAHandlerDeclaredInStartAsyncStoresTheTokenLater()
    {
        // The handler runs after the start returned, when the token is already inert; the assignment
        // is still written inside StartAsync, and it still keeps the start token.
        const string source =
            "class Broker : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        _server.OnSessionStarted += session => {\n" +
            "            _lastToken = cancellationToken;\n" +
            "            return ValueTask.CompletedTask;\n" +
            "        };\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(4);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenStartAsyncIsAnExplicitInterfaceImplementation()
    {
        const string source =
            "class Service : IHostedService {\n" +
            "    Task IHostedService.StartAsync(CancellationToken cancellationToken) {\n" +
            "        _token = cancellationToken;\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("_token = cancellationToken");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenStartAsyncIsExpressionBodied()
    {
        const string source =
            "class Service : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken ct) => Begin(_cts = CancellationTokenSource.CreateLinkedTokenSource(ct));\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("_cts = CancellationTokenSource.CreateLinkedTokenSource(ct)");
    }

    [Theory]
    [InlineData("IHostedService")]
    [InlineData("Microsoft.Extensions.Hosting.IHostedService")]
    [InlineData("global::Microsoft.Extensions.Hosting.IHostedService")]
    [InlineData("IDisposable, IHostedService")]
    [InlineData("IHostedLifecycleService")]
    [InlineData("BackgroundService")]
    public void Scan_ShouldFlag_WhenTheTypeIsAHostedServiceByAnyBaseSpelling(string baseList)
    {
        var source =
            $"class Service : {baseList} {{\n" +
            "    public override Task StartAsync(System.Threading.CancellationToken stoppingToken) {\n" +
            "        _token = stoppingToken;\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
        HostedStartTokenScanner.CountHostedStartMethods(source).Should().Be(1);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheBaseListIsOnAnotherPartOfThePartialTypeInTheSameFile()
    {
        const string source =
            "namespace N;\n" +
            "partial class Sweeper : IHostedService, IDisposable { }\n" +
            "partial class Sweeper {\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(5);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAHostedTypeIsNestedInANonHostedOne()
    {
        const string source =
            "static class Outer {\n" +
            "    public Task StartAsync(CancellationToken ct) { _outer = ct; return Task.CompletedTask; }\n" +
            "    sealed class Inner : IHostedService {\n" +
            "        public Task StartAsync(CancellationToken ct) { _inner = ct; return Task.CompletedTask; }\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("_inner = ct");
    }

    [Theory]
    [InlineData("await _server.StartAsync(cancellationToken)")]
    [InlineData("var token = cancellationToken")]
    [InlineData("CancellationToken token; token = cancellationToken")]
    [InlineData("using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)")]
    [InlineData("var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.Dispose()")]
    [InlineData("var options = new StartOptions(); options.Token = cancellationToken")]
    [InlineData("var options = new StartOptions { Token = cancellationToken }")]
    [InlineData("_ = cancellationToken")]
    [InlineData("cancellationToken.ThrowIfCancellationRequested()")]
    [InlineData("_ = Task.Run(() => LoopAsync(_cts.Token), cancellationToken)")]
    [InlineData("_cts = new CancellationTokenSource()")]
    [InlineData("_cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping)")]
    [InlineData("_token = _cts.Token")]
    [InlineData("_cts = CancellationTokenSource.CreateLinkedTokenSource(_options.cancellationToken)")]
    public void Scan_ShouldPass_WhenTheStartTokenIsOnlyUsedWithinTheStart(string statement)
    {
        var source =
            "class Service : IHostedService {\n" +
            "    public async Task StartAsync(CancellationToken cancellationToken) {\n" +
            $"        {statement};\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        HostedStartTokenScanner.CountHostedStartMethods(source).Should().Be(1, "the method was inspected and passed");
    }

    [Fact]
    public void Scan_ShouldPass_WhenANonHostedTypeHasAStartAsyncThatStoresItsToken()
    {
        // Whether R2 also binds components that are not hosted services is an open ruling; the spec
        // requirement this guard holds is about hosted services only.
        const string source =
            "class Listener : IAsyncDisposable {\n" +
            "    private CancellationTokenSource? _cts;\n" +
            "    public ValueTask StartAsync(CancellationToken cancellationToken = default) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n" +
            "        return ValueTask.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        HostedStartTokenScanner.CountHostedStartMethods(source).Should().Be(0);
    }

    [Fact]
    public void Scan_ShouldPass_WhenANonHostedTypeIsNestedInAHostedOne()
    {
        const string source =
            "sealed class Service : IHostedService {\n" +
            "    sealed class Worker {\n" +
            "        public Task StartAsync(CancellationToken ct) { _token = ct; return Task.CompletedTask; }\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldPass_WhenAnotherMethodOfTheHostedServiceStoresItsToken()
    {
        // StopAsync's token means "no longer graceful"; ADR-0059 R3 wires it onto an owned source, and
        // this rule is about the start token only.
        const string source =
            "class Service : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;\n" +
            "    public Task StopAsync(CancellationToken cancellationToken) {\n" +
            "        _stopToken = cancellationToken;\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "    public Task StartAsync() { _token = _cts.Token; return Task.CompletedTask; }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        HostedStartTokenScanner.CountHostedStartMethods(source).Should().Be(1);
    }

    [Fact]
    public void Scan_ShouldPass_WhenALambdaParameterShadowsTheTokenName()
    {
        const string source =
            "class Service : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken ct) {\n" +
            "        _server.OnRestart += ct => { _restartToken = ct; };\n" +
            "        _server.OnReload += (session, ct) => { _reloadToken = ct; };\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenAStoredStartTokenIsOnlyMentionedInCommentsOrStrings()
    {
        const string source =
            "/// <c>_stoppingToken = cancellationToken;</c> is the defect.\n" +
            "class Service : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        // _stoppingToken = cancellationToken;\n" +
            "        var s = \"_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)\";\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(DecisionGuardMatch match)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "ADR-0059 R2: a hosted service does not keep its StartAsync token. That token means the start " +
            "was aborted, and the host releases the source behind it the moment the start returns, so a " +
            "stored copy can never be cancelled again and a source linked over it couples every later use " +
            "to an aborted start.");
        sb.AppendLine(
            "Own the source instead (private readonly CancellationTokenSource _stopping = new();), cancel it " +
            "from StopAsync, and use the start token only within the start.");
        sb.Append(match.Describe());
        return sb.ToString();
    }
}
