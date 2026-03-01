using Asterisk.Sdk.Agi.Server;
using FluentAssertions;

namespace Asterisk.Sdk.Agi.Tests.Server;

public class AgiReplyTests
{
    [Fact]
    public void Parse_SuccessResult()
    {
        var reply = AgiReply.Parse("200 result=0");
        reply.StatusCode.Should().Be(200);
        reply.IsSuccess.Should().BeTrue();
        reply.Result.Should().Be("0");
    }

    [Fact]
    public void Parse_SuccessWithExtra()
    {
        var reply = AgiReply.Parse("200 result=1 (speech)");
        reply.StatusCode.Should().Be(200);
        reply.Result.Should().Be("1");
        reply.Extra.Should().Be("speech");
    }

    [Fact]
    public void Parse_InvalidCommand()
    {
        var reply = AgiReply.Parse("510 Invalid or unknown command");
        reply.StatusCode.Should().Be(510);
        reply.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Parse_DigitResult()
    {
        var reply = AgiReply.Parse("200 result=42");
        reply.ResultAsInt.Should().Be(42);
    }

    [Fact]
    public void Parse_VariableWithParentheses()
    {
        var reply = AgiReply.Parse("200 result=1 (hello world)");
        reply.Extra.Should().Be("hello world");
    }
}
