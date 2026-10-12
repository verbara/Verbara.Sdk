using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for the spec requirement "A component whose start returns while its work runs does not keep
/// its start token": no type in <c>src/</c> that is not a hosted service assigns the token of its
/// <c>StartAsync</c> or <c>ConnectAsync</c> to a field, or a linked source over it to a field, unless the method
/// awaits the work it linked. It lands with no baseline entry: every site it reports must be fixed, and the tree
/// must match the (empty) list exactly. Carries a liveness floor over the bodied methods it inspects and detector
/// fixtures for every shape the scanner claims to report and every shape it must pass.
/// </summary>
public sealed class ComponentStartTokenGuardTests
{
    // Conservative floor, below the tree as this guard landed (14 bodied non-hosted methods taking a token, measured:
    // 8 StartAsync — four servers, the live server, three activity bases — and 6 ConnectAsync — the AMI connection,
    // its pipeline transport, the ARI client, the AudioSocket client and two static WebSocket connect helpers). With
    // no baseline entry, a detector that stopped recognising these methods would otherwise pass having inspected
    // nothing.
    private const int MinimumComponentStartMethods = 8;

    [Fact]
    public void Guard_ShouldMatchTheBaselineExactly_WhenScanningTheSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

        var sites = new List<DecisionGuardSite>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
            sites.AddRange(HostedStartTokenScanner.ScanComponents(File.ReadAllText(file), ToRelative(repoRoot, file)));

        var match = DecisionGuardBaseline.LoadCommitted().Match(HostedStartTokenScanner.ComponentGuardName, sites);

        match.IsExact.Should().BeTrue(BuildFailureMessage(match));
    }

    [Fact]
    public void Guard_ShouldInspectManyComponentStartMethods_WhenWalkingTheSrcTree()
    {
        var methods = SrcTreeSource.EnumerateSrcSources()
            .Sum(file => HostedStartTokenScanner.CountComponentStartMethods(File.ReadAllText(file)));

        methods.Should().BeGreaterThanOrEqualTo(
            MinimumComponentStartMethods,
            "the detector must see the starts and connects src/ actually declares; finding almost none means the " +
            "type or method detection broke, and an empty baseline would then pass having inspected nothing");
    }

    [Fact]
    public void GuardName_ShouldBeRegistered_WhenTheBaselineIsRead()
    {
        DecisionGuardBaseline.RegisteredGuards.Should().Contain(
            HostedStartTokenScanner.ComponentGuardName,
            "an unregistered guard's baseline entries are reported as orphans");
    }

    // ------------------------------------------------------------------------------------- reach

    [Fact]
    public void ScanComponents_ShouldFlag_WhenAServerLinksItsStartTokenIntoItsAcceptLoopsSource()
    {
        const string source =
            "class Server : IAsyncDisposable {\n" +
            "    private CancellationTokenSource? _cts;\n" +
            "    public ValueTask StartAsync(CancellationToken ct = default) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);\n" +
            "        _acceptLoop = AcceptLoopAsync(_cts.Token);\n" +
            "        return ValueTask.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle().Which.Should().Be(
            new DecisionGuardSite("x.cs", 4, "_cts = CancellationTokenSource.CreateLinkedTokenSource(ct)"));
        HostedStartTokenScanner.CountComponentStartMethods(source).Should().Be(1);
    }

    [Fact]
    public void ScanComponents_ShouldFlag_WhenAConnectLinksItsTokenIntoAKeptPumpSource()
    {
        const string source =
            "public sealed class Connection {\n" +
            "    public async ValueTask ConnectAsync(string host, int port, bool useSsl = false, CancellationToken cancellationToken = default) {\n" +
            "        await _client.ConnectAsync(host, port, cancellationToken);\n" +
            "        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n" +
            "        var token = _pumpCts.Token;\n" +
            "        _readPumpTask = Task.Run(() => ReadPumpAsync(token), CancellationToken.None);\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be(
            "_pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)");
    }

    [Fact]
    public void ScanComponents_ShouldFlag_WhenStartAsyncIsAnExplicitInterfaceImplementation()
    {
        const string source =
            "class Listener : IListener {\n" +
            "    ValueTask IListener.StartAsync(CancellationToken ct) {\n" +
            "        _token = ct;\n" +
            "        return ValueTask.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("_token = ct");
    }

    [Fact]
    public void ScanComponents_ShouldFlag_WhenStartAsyncIsExpressionBodied()
    {
        const string source =
            "class Listener {\n" +
            "    public Task StartAsync(CancellationToken ct) => Begin(_cts = CancellationTokenSource.CreateLinkedTokenSource(ct));\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("_cts = CancellationTokenSource.CreateLinkedTokenSource(ct)");
    }

    [Fact]
    public void ScanComponents_ShouldFlag_WhenANonHostedTypeHasAStartAsyncThatStoresItsToken()
    {
        // The twin of the hosted guard's boundary fixture of the same name: the hosted guard passes it, this one
        // reports it. The method assigns the member and never reads it, so it fires by the zero-read rule.
        const string source =
            "class Listener : IAsyncDisposable {\n" +
            "    private CancellationTokenSource? _cts;\n" +
            "    public ValueTask StartAsync(CancellationToken cancellationToken = default) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n" +
            "        return ValueTask.CompletedTask;\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(4);
        HostedStartTokenScanner.Scan(source, "x.cs").Should().BeEmpty("the hosted guard's boundary is unchanged");
    }

    [Fact]
    public void ScanComponents_ShouldFlag_WhenANonHostedTypeIsNestedInAHostedOne()
    {
        // The twin of the hosted guard's boundary fixture of the same name; zero reads in the method.
        const string source =
            "sealed class Service : IHostedService {\n" +
            "    sealed class Worker {\n" +
            "        public Task StartAsync(CancellationToken ct) { _token = ct; return Task.CompletedTask; }\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("_token = ct");
        HostedStartTokenScanner.Scan(source, "x.cs").Should().BeEmpty("the hosted guard's boundary is unchanged");
    }

    [Fact]
    public void ScanComponents_ShouldNotReach_WhenTheTypeIsAHostedService()
    {
        const string source =
            "class Sweeper : IHostedService {\n" +
            "    public Task StartAsync(CancellationToken cancellationToken) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n" +
            "        return Task.CompletedTask;\n" +
            "    }\n" +
            "}";

        HostedStartTokenScanner.ScanComponents(source, "x.cs").Should().BeEmpty("a hosted service is the hosted guard's");
        HostedStartTokenScanner.CountComponentStartMethods(source).Should().Be(0);
    }

    [Fact]
    public void CountComponentStartMethods_ShouldCountOnlyBodiedMethods_WhenSomeAreDeclarationsWithoutABody()
    {
        const string source =
            "interface IListener { ValueTask StartAsync(CancellationToken ct = default); }\n" +
            "abstract class Base { public abstract Task ConnectAsync(CancellationToken ct); }\n" +
            "class Listener : IListener {\n" +
            "    public ValueTask StartAsync(CancellationToken ct = default) => ValueTask.CompletedTask;\n" +
            "    public Task ConnectAsync() => Task.CompletedTask;\n" +
            "}";

        HostedStartTokenScanner.CountComponentStartMethods(source).Should().Be(
            1, "a declaration without a body keeps nothing, and a method without a token has nothing to keep");
    }

    // ------------------------------------------------------------------------- the start that is the life

    [Theory]
    [InlineData("await ExecuteAsync(_executionCts.Token)")]
    [InlineData("await ExecuteAsync(this._executionCts.Token)")]
    [InlineData("await ExecuteAsync(_executionCts.Token); await FinishAsync(_executionCts.Token)")]
    public void ScanComponents_ShouldPass_WhenTheStartAwaitsTheWorkItLinks(string statements)
    {
        var source =
            "abstract class ActivityBase {\n" +
            "    private CancellationTokenSource? _executionCts;\n" +
            "    public async ValueTask StartAsync(CancellationToken cancellationToken = default) {\n" +
            "        lock (_lock) { _executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); }\n" +
            $"        try {{ {statements}; }}\n" +
            "        catch (OperationCanceledException) { }\n" +
            "    }\n" +
            "    public ValueTask CancelAsync() { _executionCts?.Cancel(); return ValueTask.CompletedTask; }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().BeEmpty("the work runs inside the start, so the token's phase is the work's phase");
        HostedStartTokenScanner.CountComponentStartMethods(source).Should().Be(1, "the method was inspected and passed");
    }

    [Theory]
    [InlineData("_ = Task.Run(() => LoopAsync(_executionCts.Token))")]
    [InlineData("_loop = LoopAsync(_executionCts.Token)")]
    [InlineData("_ = LoopAsync(_executionCts.Token)")]
    [InlineData("_ = Task.Run(async () => await LoopAsync(_executionCts.Token))")]
    [InlineData("_executionCts?.Cancel()")]
    public void ScanComponents_ShouldFlag_WhenTheStartAwaitsInOnePlaceAndHandsOffInAnother(string handOff)
    {
        var source =
            "class Component {\n" +
            "    public async ValueTask StartAsync(CancellationToken cancellationToken = default) {\n" +
            "        _executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n" +
            "        await ExecuteAsync(_executionCts.Token);\n" +
            $"        {handOff};\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle("one read that is not an awaited argument is a loop that outlives the start")
            .Which.Line.Should().Be(3);
    }

    [Fact]
    public void ScanComponents_ShouldFlag_WhenTheStartKeepsTheSourceAndLeavesTheReadToTheLoop()
    {
        const string source =
            "class Server {\n" +
            "    public ValueTask StartAsync(CancellationToken ct) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);\n" +
            "        _loop = RunAsync();\n" +
            "        return ValueTask.CompletedTask;\n" +
            "    }\n" +
            "    private async Task RunAsync() { await Task.Yield(); _cts!.Token.ThrowIfCancellationRequested(); }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle("zero reads in the method is the defect rewritten one call away")
            .Which.Key.Should().Be("_cts = CancellationTokenSource.CreateLinkedTokenSource(ct)");
    }

    [Theory]
    [InlineData("await Task.WhenAny(_loop = AcceptLoopAsync(_cts.Token), Task.CompletedTask)")]
    [InlineData("await Task.FromResult(_loop = Loop(_cts.Token))")]
    [InlineData("await Task.WhenAll(AcceptLoopAsync(_cts.Token))")]
    public void ScanComponents_ShouldFlag_WhenTheReadIsNestedUnderAnAwaitedCombinator(string statement)
    {
        var source =
            "class Server {\n" +
            "    public async ValueTask StartAsync(CancellationToken ct) {\n" +
            "        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);\n" +
            $"        {statement};\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().ContainSingle("the awaited call is the combinator, not the loop it was handed").Which.Line.Should().Be(3);
    }

    // ------------------------------------------------------------------------------ uses within the phase

    [Theory]
    [InlineData("_cts = new CancellationTokenSource(); _loop = AcceptLoopAsync(_cts.Token)")]
    [InlineData("await _client.ConnectAsync(host, cancellationToken)")]
    [InlineData("using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)")]
    [InlineData("cancellationToken.ThrowIfCancellationRequested()")]
    [InlineData("_ = cancellationToken")]
    [InlineData("var token = cancellationToken")]
    public void ScanComponents_ShouldPass_WhenTheTokenIsOnlyUsedWithinTheCall(string statement)
    {
        var source =
            "class Server {\n" +
            "    public async ValueTask StartAsync(string host, CancellationToken cancellationToken = default) {\n" +
            $"        {statement};\n" +
            "    }\n" +
            "}";

        var sites = HostedStartTokenScanner.ScanComponents(source, "x.cs");

        sites.Should().BeEmpty();
        HostedStartTokenScanner.CountComponentStartMethods(source).Should().Be(1, "the method was inspected and passed");
    }

    [Fact]
    public void ScanComponents_ShouldPass_WhenAnotherMethodOfTheComponentStoresAToken()
    {
        const string source =
            "class Server {\n" +
            "    public ValueTask StopAsync(CancellationToken ct) { _stopToken = ct; return ValueTask.CompletedTask; }\n" +
            "    public ValueTask RunAsync(CancellationToken ct) { _cts = CancellationTokenSource.CreateLinkedTokenSource(ct); return ValueTask.CompletedTask; }\n" +
            "}";

        HostedStartTokenScanner.ScanComponents(source, "x.cs").Should().BeEmpty("the rule is about the start and the connect only");
        HostedStartTokenScanner.CountComponentStartMethods(source).Should().Be(0);
    }

    [Fact]
    public void ScanComponents_ShouldIgnore_WhenAKeptStartTokenIsOnlyMentionedInCommentsOrStrings()
    {
        const string source =
            "/// <c>_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);</c> is the defect.\n" +
            "class Server {\n" +
            "    public ValueTask StartAsync(CancellationToken ct) {\n" +
            "        // _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);\n" +
            "        var s = \"_cts = CancellationTokenSource.CreateLinkedTokenSource(ct)\";\n" +
            "        return ValueTask.CompletedTask;\n" +
            "    }\n" +
            "}";

        HostedStartTokenScanner.ScanComponents(source, "x.cs").Should().BeEmpty();
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(DecisionGuardMatch match)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "A component whose start or connect returns while its work runs does not keep the token it was handed: " +
            "that token bounds the start, not the life. Linked into the source the component's loop runs on, a " +
            "cancel after the start returned ends the loop while the component goes on reporting itself running.");
        sb.AppendLine(
            "Own the source instead (_cts = new CancellationTokenSource();), cancel it from the stop, close or " +
            "disposal, and use the token only within the call.");
        sb.Append(match.Describe());
        return sb.ToString();
    }
}
