using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Agi.Mapping;
using Asterisk.NetAot.Agi.Server;
using FluentAssertions;
using NSubstitute;

namespace Asterisk.NetAot.Agi.Tests.Server;

public class MappingStrategyTests
{
    private static IAgiRequest CreateRequest(string script)
    {
        var request = Substitute.For<IAgiRequest>();
        request.Script.Returns(script);
        return request;
    }

    [Fact]
    public void SimpleMappingStrategy_ExactMatch()
    {
        var strategy = new SimpleMappingStrategy();
        var script = Substitute.For<IAgiScript>();
        strategy.Add("Hello", script);

        var result = strategy.Resolve(CreateRequest("Hello"));

        result.Should().BeSameAs(script);
    }

    [Fact]
    public void SimpleMappingStrategy_PathStripping()
    {
        var strategy = new SimpleMappingStrategy();
        var script = Substitute.For<IAgiScript>();
        strategy.Add("Hello", script);

        var result = strategy.Resolve(CreateRequest("agi://192.168.1.1/Hello"));

        result.Should().BeSameAs(script);
    }

    [Fact]
    public void SimpleMappingStrategy_NoMatch_ReturnsNull()
    {
        var strategy = new SimpleMappingStrategy();

        var result = strategy.Resolve(CreateRequest("Unknown"));

        result.Should().BeNull();
    }

    [Fact]
    public void CompositeMappingStrategy_TriesInOrder()
    {
        var strategy1 = new SimpleMappingStrategy();
        var strategy2 = new SimpleMappingStrategy();
        var script2 = Substitute.For<IAgiScript>();
        strategy2.Add("Fallback", script2);

        var composite = new CompositeMappingStrategy(strategy1, strategy2);

        var result = composite.Resolve(CreateRequest("Fallback"));

        result.Should().BeSameAs(script2);
    }

    [Fact]
    public void TypeNameMappingStrategy_MatchesByTypeName()
    {
        var strategy = new TypeNameMappingStrategy();
        var script = Substitute.For<IAgiScript>();
        strategy.Register("MyScript", () => script);

        var result = strategy.Resolve(CreateRequest("agi://host/MyScript"));

        result.Should().BeSameAs(script);
    }

    [Fact]
    public void TypeNameMappingStrategy_StripsQueryParams()
    {
        var strategy = new TypeNameMappingStrategy();
        var script = Substitute.For<IAgiScript>();
        strategy.Register("MyScript", () => script);

        var result = strategy.Resolve(CreateRequest("agi://host/MyScript?param=value"));

        result.Should().BeSameAs(script);
    }
}
