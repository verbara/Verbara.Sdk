using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using FluentAssertions;
using NSubstitute;

namespace Asterisk.Sdk.Agi.Tests.Server;

public class SimpleMappingStrategyTests
{
    private static IAgiRequest CreateRequest(string? script)
    {
        var request = Substitute.For<IAgiRequest>();
        request.Script.Returns(script);
        return request;
    }

    [Fact]
    public void Resolve_ShouldReturnScript_WhenPathPrefixMatchesRegisteredName()
    {
        var strategy = new SimpleMappingStrategy();
        var script = Substitute.For<IAgiScript>();
        strategy.Add("Greeting", script);

        var result = strategy.Resolve(CreateRequest("agi://10.0.0.1/Greeting"));

        result.Should().BeSameAs(script);
    }

    [Fact]
    public void Resolve_ShouldReturnScript_WhenQueryStringIsStripped()
    {
        var strategy = new SimpleMappingStrategy();
        var script = Substitute.For<IAgiScript>();
        strategy.Add("Router.agi", script);

        var result = strategy.Resolve(CreateRequest("agi://host/Router.agi?param=value&other=1"));

        result.Should().BeSameAs(script);
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenScriptIsNull()
    {
        var strategy = new SimpleMappingStrategy();
        strategy.Add("Something", Substitute.For<IAgiScript>());

        var result = strategy.Resolve(CreateRequest(null));

        result.Should().BeNull();
    }

    [Fact]
    public void Remove_ShouldReturnTrue_WhenScriptExists()
    {
        var strategy = new SimpleMappingStrategy();
        strategy.Add("ToRemove", Substitute.For<IAgiScript>());

        var removed = strategy.Remove("ToRemove");

        removed.Should().BeTrue();
    }

    [Fact]
    public void Remove_ShouldReturnFalse_WhenScriptDoesNotExist()
    {
        var strategy = new SimpleMappingStrategy();

        var removed = strategy.Remove("NonExistent");

        removed.Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldMakeScriptUnresolvable()
    {
        var strategy = new SimpleMappingStrategy();
        strategy.Add("Temp", Substitute.For<IAgiScript>());
        strategy.Remove("Temp");

        var result = strategy.Resolve(CreateRequest("Temp"));

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldBeCaseInsensitive()
    {
        var strategy = new SimpleMappingStrategy();
        var script = Substitute.For<IAgiScript>();
        strategy.Add("MyScript", script);

        var result = strategy.Resolve(CreateRequest("myscript"));

        result.Should().BeSameAs(script);
    }
}
