using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

public sealed class RealtimeFunctionRegistryTests
{
    private sealed class StubHandler : IRealtimeFunctionHandler
    {
        public StubHandler(string name) => Name = name;
        public string Name { get; }
        public string Description => $"Stub: {Name}";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("{}");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDuplicateHandlersRegistered()
    {
        var handlers = new IRealtimeFunctionHandler[]
        {
            new StubHandler("get_time"),
            new StubHandler("get_time"),
        };

        var act = () => new RealtimeFunctionRegistry(handlers);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*get_time*");
    }

    [Fact]
    public void Constructor_ShouldAcceptEmptyHandlers()
    {
        var registry = new RealtimeFunctionRegistry([]);

        registry.AllHandlers.Should().BeEmpty();
    }

    [Fact]
    public void TryGetHandler_ShouldBeCaseSensitive()
    {
        var registry = new RealtimeFunctionRegistry([new StubHandler("GetTime")]);

        registry.TryGetHandler("GetTime", out _).Should().BeTrue();
        registry.TryGetHandler("gettime", out _).Should().BeFalse();
        registry.TryGetHandler("GETTIME", out _).Should().BeFalse();
        registry.TryGetHandler("getTime", out _).Should().BeFalse();
    }

    [Fact]
    public void AllHandlers_ShouldContainAllRegistered()
    {
        var handlers = new IRealtimeFunctionHandler[]
        {
            new StubHandler("func_a"),
            new StubHandler("func_b"),
            new StubHandler("func_c"),
        };

        var registry = new RealtimeFunctionRegistry(handlers);

        registry.AllHandlers.Should().HaveCount(3);
        registry.AllHandlers.Select(h => h.Name).Should()
            .BeEquivalentTo(["func_a", "func_b", "func_c"]);
    }

    [Fact]
    public void TryGetHandler_ShouldReturnCorrectHandler_WhenMultipleRegistered()
    {
        var handlerA = new StubHandler("alpha");
        var handlerB = new StubHandler("beta");
        var registry = new RealtimeFunctionRegistry([handlerA, handlerB]);

        registry.TryGetHandler("alpha", out var found).Should().BeTrue();
        found.Should().BeSameAs(handlerA);

        registry.TryGetHandler("beta", out var foundB).Should().BeTrue();
        foundB.Should().BeSameAs(handlerB);
    }

    [Fact]
    public void TryGetHandler_ShouldReturnFalse_ForEmptyRegistry()
    {
        var registry = new RealtimeFunctionRegistry([]);

        registry.TryGetHandler("anything", out var handler).Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void Constructor_ShouldAcceptSingleHandler()
    {
        var handler = new StubHandler("solo");
        var registry = new RealtimeFunctionRegistry([handler]);

        registry.AllHandlers.Should().ContainSingle()
            .Which.Name.Should().Be("solo");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDuplicateHandlersHaveDifferentInstances()
    {
        // Two different instances with the same Name
        var h1 = new StubHandler("lookup");
        var h2 = new StubHandler("lookup");

        var act = () => new RealtimeFunctionRegistry([h1, h2]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*lookup*");
    }
}
