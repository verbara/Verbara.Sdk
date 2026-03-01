using Asterisk.NetAot.Agi.Commands;
using FluentAssertions;

namespace Asterisk.NetAot.Agi.Tests.Commands;

public class AgiCommandTests
{
    [Fact]
    public void AnswerCommand_BuildCommand_ShouldReturnAnswer()
    {
        var cmd = new AnswerCommand();
        cmd.BuildCommand().Should().Be("ANSWER");
    }

    [Fact]
    public void HangupCommand_BuildCommand_ShouldReturnHangup()
    {
        var cmd = new HangupCommand();
        cmd.BuildCommand().Should().Be("HANGUP");
    }

    [Fact]
    public void HangupCommand_ShouldHaveChannelProperty()
    {
        var cmd = new HangupCommand { Channel = "SIP/2000-0001" };
        cmd.Channel.Should().Be("SIP/2000-0001");
    }

    [Fact]
    public void StreamFileCommand_BuildCommand_ShouldReturnStreamFile()
    {
        var cmd = new StreamFileCommand { File = "hello-world", EscapeDigits = "#" };
        cmd.BuildCommand().Should().StartWith("STREAM FILE");
    }

    [Fact]
    public void StreamFileCommand_ShouldHaveProperties()
    {
        var cmd = new StreamFileCommand
        {
            File = "beep",
            EscapeDigits = "0123456789",
            Offset = 5000
        };

        cmd.File.Should().Be("beep");
        cmd.EscapeDigits.Should().Be("0123456789");
        cmd.Offset.Should().Be(5000);
    }

    [Fact]
    public void GetVariableCommand_BuildCommand_ShouldReturnGetVariable()
    {
        var cmd = new GetVariableCommand { Variable = "CALLERID(num)" };
        cmd.BuildCommand().Should().StartWith("GET VARIABLE");
    }

    [Fact]
    public void SetVariableCommand_BuildCommand_ShouldReturnSetVariable()
    {
        var cmd = new SetVariableCommand { Variable = "MYVAR", Value = "hello" };
        cmd.BuildCommand().Should().StartWith("SET VARIABLE");
    }

    [Fact]
    public void ExecCommand_BuildCommand_ShouldReturnExec()
    {
        var cmd = new ExecCommand { Application = "Dial" };
        cmd.BuildCommand().Should().StartWith("EXEC");
    }

    [Fact]
    public void GetDataCommand_BuildCommand_ShouldReturnGetData()
    {
        var cmd = new GetDataCommand { File = "demo-congrats", Timeout = 5000, MaxDigits = 4 };
        cmd.BuildCommand().Should().StartWith("GET DATA");
    }

    [Fact]
    public void GetDataCommand_ShouldHaveProperties()
    {
        var cmd = new GetDataCommand { File = "vm-enter-num-to-call", Timeout = 10000, MaxDigits = 10 };
        cmd.File.Should().Be("vm-enter-num-to-call");
        cmd.Timeout.Should().Be(10000);
        cmd.MaxDigits.Should().Be(10);
    }

    [Fact]
    public void AllCommands_ShouldInheritFromAgiCommandBase()
    {
        new AnswerCommand().Should().BeAssignableTo<AgiCommandBase>();
        new HangupCommand().Should().BeAssignableTo<AgiCommandBase>();
        new StreamFileCommand().Should().BeAssignableTo<AgiCommandBase>();
        new GetVariableCommand().Should().BeAssignableTo<AgiCommandBase>();
        new SetVariableCommand().Should().BeAssignableTo<AgiCommandBase>();
        new ExecCommand().Should().BeAssignableTo<AgiCommandBase>();
        new GetDataCommand().Should().BeAssignableTo<AgiCommandBase>();
        new NoopCommand().Should().BeAssignableTo<AgiCommandBase>();
        new VerboseCommand().Should().BeAssignableTo<AgiCommandBase>();
        new WaitForDigitCommand().Should().BeAssignableTo<AgiCommandBase>();
    }
}
