using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

public sealed class FunctionCallTests
{
    private sealed class AddFunction : IRealtimeFunctionHandler
    {
        public string Name => "add";
        public string Description => "Adds two numbers";
        public string ParametersSchema => """{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("""{"result":42}""");
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsRegisteredHandler()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("add", out var handler);

        found.Should().BeTrue();
        handler.Should().NotBeNull();
        handler!.Name.Should().Be("add");
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsFalseForUnknown()
    {
        var registry = new RealtimeFunctionRegistry([new AddFunction()]);
        var found = registry.TryGetHandler("unknown", out var handler);

        found.Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void Registry_DuplicateName_Throws()
    {
        var act = () => new RealtimeFunctionRegistry([new AddFunction(), new AddFunction()]);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'add'*");
    }

    [Fact]
    public void Registry_AllHandlers_ContainsRegisteredHandlers()
    {
        var handler = new AddFunction();
        var registry = new RealtimeFunctionRegistry([handler]);

        registry.AllHandlers.Should().ContainSingle()
            .Which.Name.Should().Be("add");
    }
}
