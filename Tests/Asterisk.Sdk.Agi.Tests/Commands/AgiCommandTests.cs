using Asterisk.Sdk.Agi.Commands;
using FluentAssertions;

namespace Asterisk.Sdk.Agi.Tests.Commands;

#pragma warning disable CA1707 // Identifiers should not contain underscores

public class ControlStreamFileCommandTests
{
    [Theory]
    [InlineData("welcome", "#", null, null, null, null, "CONTROL STREAM FILE welcome #")]
    [InlineData("welcome", "#", 3000, null, null, null, "CONTROL STREAM FILE welcome # 3000")]
    [InlineData("welcome", "#", 3000, "*", null, null, "CONTROL STREAM FILE welcome # 3000 *")]
    [InlineData("welcome", "#", 3000, "*", "#", null, "CONTROL STREAM FILE welcome # 3000 * #")]
    [InlineData("welcome", "#", 3000, "*", "#", "0", "CONTROL STREAM FILE welcome # 3000 * # 0")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string file, string escapeDigits, int? offset, string? forward, string? rewind, string? pause,
        string expected)
    {
        var cmd = new ControlStreamFileCommand
        {
            File = file,
            EscapeDigits = escapeDigits,
            Offset = offset,
            ForwardDigit = forward,
            RewindDigit = rewind,
            PauseDigit = pause
        };

        cmd.BuildCommand().Should().Be(expected);
    }

    [Fact]
    public void BuildCommand_ShouldIgnoreForward_WhenOffsetIsNull()
    {
        var cmd = new ControlStreamFileCommand
        {
            File = "test",
            EscapeDigits = "",
            ForwardDigit = "*"
        };

        cmd.BuildCommand().Should().Be("CONTROL STREAM FILE test ");
    }

    [Fact]
    public void BuildCommand_ShouldIgnoreRewind_WhenForwardIsNull()
    {
        var cmd = new ControlStreamFileCommand
        {
            File = "test",
            EscapeDigits = "#",
            Offset = 1000,
            RewindDigit = "#"
        };

        cmd.BuildCommand().Should().Be("CONTROL STREAM FILE test # 1000");
    }

    [Fact]
    public void BuildCommand_ShouldIgnorePause_WhenRewindIsNull()
    {
        var cmd = new ControlStreamFileCommand
        {
            File = "test",
            EscapeDigits = "#",
            Offset = 1000,
            ForwardDigit = "*",
            PauseDigit = "0"
        };

        cmd.BuildCommand().Should().Be("CONTROL STREAM FILE test # 1000 *");
    }
}

public class RecordFileCommandTests
{
    [Theory]
    [InlineData("rec", "wav", "#", 5000, null, null, null, "RECORD FILE rec wav # 5000")]
    [InlineData("rec", "wav", "#", null, null, null, null, "RECORD FILE rec wav # -1")]
    [InlineData("rec", "wav", "#", 5000, 100, null, null, "RECORD FILE rec wav # 5000 100")]
    [InlineData("rec", "wav", "#", 5000, 100, true, null, "RECORD FILE rec wav # 5000 100 BEEP")]
    [InlineData("rec", "wav", "#", 5000, 100, false, null, "RECORD FILE rec wav # 5000 100")]
    [InlineData("rec", "wav", "#", 5000, 100, true, 3, "RECORD FILE rec wav # 5000 100 BEEP s=3")]
    [InlineData("rec", "wav", "#", 5000, 100, false, 3, "RECORD FILE rec wav # 5000 100 s=3")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string file, string format, string escapeDigits, int? timeout, int? offset,
        bool? beep, int? maxSilence, string expected)
    {
        var cmd = new RecordFileCommand
        {
            File = file,
            Format = format,
            EscapeDigits = escapeDigits,
            Timeout = timeout,
            Offset = offset,
            Beep = beep,
            MaxSilence = maxSilence
        };

        cmd.BuildCommand().Should().Be(expected);
    }

    [Fact]
    public void BuildCommand_ShouldIgnoreBeep_WhenOffsetIsNull()
    {
        var cmd = new RecordFileCommand
        {
            File = "rec",
            Format = "wav",
            EscapeDigits = "#",
            Timeout = 5000,
            Beep = true
        };

        cmd.BuildCommand().Should().Be("RECORD FILE rec wav # 5000");
    }

    [Fact]
    public void BuildCommand_ShouldIgnoreMaxSilence_WhenBeepIsNull()
    {
        var cmd = new RecordFileCommand
        {
            File = "rec",
            Format = "wav",
            EscapeDigits = "#",
            Timeout = 5000,
            Offset = 0,
            MaxSilence = 5
        };

        cmd.BuildCommand().Should().Be("RECORD FILE rec wav # 5000 0");
    }
}

public class DialCommandTests
{
    [Theory]
    [InlineData("SIP/100", null, null, "EXEC Dial SIP/100")]
    [InlineData("SIP/100", 30, null, "EXEC Dial SIP/100,30")]
    [InlineData("SIP/100", null, "tT", "EXEC Dial SIP/100,0,tT")]
    [InlineData("SIP/100", 30, "tTm", "EXEC Dial SIP/100,30,tTm")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string target, int? timeout, string? options, string expected)
    {
        var cmd = new DialCommand
        {
            Target = target,
            Timeout = timeout,
            Options = options
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class ConfbridgeCommandTests
{
    [Theory]
    [InlineData("100", null, null, null, "EXEC ConfBridge 100")]
    [InlineData("100", "mybridge", null, null, "EXEC ConfBridge 100,mybridge")]
    [InlineData("100", null, "myuser", null, "EXEC ConfBridge 100,,myuser")]
    [InlineData("100", null, null, "mymenu", "EXEC ConfBridge 100,,,mymenu")]
    [InlineData("100", "bp", "up", null, "EXEC ConfBridge 100,bp,up")]
    [InlineData("100", "bp", "up", "menu", "EXEC ConfBridge 100,bp,up,menu")]
    [InlineData("100", null, "up", "menu", "EXEC ConfBridge 100,,up,menu")]
    [InlineData("100", "bp", null, "menu", "EXEC ConfBridge 100,bp,,menu")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string conference, string? bridgeProfile, string? userProfile, string? menu,
        string expected)
    {
        var cmd = new ConfbridgeCommand
        {
            Conference = conference,
            BridgeProfile = bridgeProfile,
            UserProfile = userProfile,
            Menu = menu
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class GosubCommandTests
{
    [Theory]
    [InlineData("default", "s", "1", null, "GOSUB default s 1")]
    [InlineData("default", "s", "1", "arg1", "GOSUB default s 1 arg1")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string context, string extension, string priority, string? optionalArg,
        string expected)
    {
        var cmd = new GosubCommand
        {
            Context = context,
            Extension = extension,
            Priority = priority,
            OptionalArg = optionalArg
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class GetOptionCommandTests
{
    [Theory]
    [InlineData("prompt", "#", null, "GET OPTION prompt #")]
    [InlineData("prompt", "#", 5000L, "GET OPTION prompt # 5000")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string file, string escapeDigits, long? timeout, string expected)
    {
        var cmd = new GetOptionCommand
        {
            File = file,
            EscapeDigits = escapeDigits,
            Timeout = timeout
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class SpeechRecognizeCommandTests
{
    [Theory]
    [InlineData("hello", null, null, "SPEECH RECOGNIZE hello 0")]
    [InlineData("hello", 5000, null, "SPEECH RECOGNIZE hello 5000")]
    [InlineData("hello", 5000, 100, "SPEECH RECOGNIZE hello 5000 100")]
    [InlineData("hello", null, 100, "SPEECH RECOGNIZE hello 0 100")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string prompt, int? timeout, int? offset, string expected)
    {
        var cmd = new SpeechRecognizeCommand
        {
            Prompt = prompt,
            Timeout = timeout,
            Offset = offset
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class MeetmeCommandTests
{
    [Theory]
    [InlineData("100", null, null, "EXEC MeetMe 100")]
    [InlineData("100", "dM", null, "EXEC MeetMe 100,dM")]
    [InlineData("100", null, "1234", "EXEC MeetMe 100,,1234")]
    [InlineData("100", "dM", "1234", "EXEC MeetMe 100,dM,1234")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string conference, string? options, string? pin, string expected)
    {
        var cmd = new MeetmeCommand
        {
            Conference = conference,
            Options = options,
            Pin = pin
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class SayDateTimeCommandTests
{
    [Theory]
    [InlineData(1000000L, "#", null, null, "SAY DATETIME 1000000 #")]
    [InlineData(1000000L, "#", "ABdY", null, "SAY DATETIME 1000000 # ABdY")]
    [InlineData(1000000L, "#", "ABdY", "UTC", "SAY DATETIME 1000000 # ABdY UTC")]
    [InlineData(null, "#", null, null, "SAY DATETIME 0 #")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        long? time, string escapeDigits, string? format, string? timezone,
        string expected)
    {
        var cmd = new SayDateTimeCommand
        {
            Time = time,
            EscapeDigits = escapeDigits,
            Format = format,
            Timezone = timezone
        };

        cmd.BuildCommand().Should().Be(expected);
    }

    [Fact]
    public void BuildCommand_ShouldIgnoreTimezone_WhenFormatIsNull()
    {
        var cmd = new SayDateTimeCommand
        {
            Time = 1000000,
            EscapeDigits = "#",
            Timezone = "UTC"
        };

        cmd.BuildCommand().Should().Be("SAY DATETIME 1000000 #");
    }
}

public class GetDataCommandTests
{
    [Theory]
    [InlineData("prompt", null, null, "GET DATA prompt")]
    [InlineData("prompt", 5000L, null, "GET DATA prompt 5000")]
    [InlineData("prompt", 5000L, 4, "GET DATA prompt 5000 4")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string file, long? timeout, int? maxDigits, string expected)
    {
        var cmd = new GetDataCommand
        {
            File = file,
            Timeout = timeout,
            MaxDigits = maxDigits
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class StreamFileCommandTests
{
    [Theory]
    [InlineData("hello", "#", null, "STREAM FILE hello #")]
    [InlineData("hello", "#", 5000, "STREAM FILE hello # 5000")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string file, string escapeDigits, int? offset, string expected)
    {
        var cmd = new StreamFileCommand
        {
            File = file,
            EscapeDigits = escapeDigits,
            Offset = offset
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class VerboseCommandTests
{
    [Theory]
    [InlineData("hello world", null, "VERBOSE \"hello world\"")]
    [InlineData("hello world", 3, "VERBOSE \"hello world\" 3")]
    [InlineData("say \"hi\"", null, "VERBOSE \"say \\\"hi\\\"\"")]
    public void BuildCommand_ShouldBuildCorrectly_WhenOptionalParamsVary(
        string message, int? level, string expected)
    {
        var cmd = new VerboseCommand
        {
            Message = message,
            Level = level
        };

        cmd.BuildCommand().Should().Be(expected);
    }
}

public class SimpleCommandBatchTests
{
    public static TheoryData<AgiCommandBase, string> SimpleCommandData => new()
    {
        // Parameterless commands
        { new AnswerCommand(), "ANSWER" },
        { new AsyncAgiBreakCommand(), "ASYNCAGI BREAK" },
        { new NoopCommand(), "NOOP" },
        { new SetMusicOffCommand(), "SET MUSIC OFF" },
        { new SpeechDestroyCommand(), "SPEECH DESTROY" },

        // Commands with required params only
        { new SayDigitsCommand { Digits = "12345", EscapeDigits = "#" }, "SAY DIGITS 12345 #" },
        { new SayAlphaCommand { Text = "hello", EscapeDigits = "#" }, "SAY ALPHA hello #" },
        { new SayNumberCommand { Number = "42", EscapeDigits = "#" }, "SAY NUMBER 42 #" },
        { new SayPhoneticCommand { Text = "alpha", EscapeDigits = "#" }, "SAY PHONETIC alpha #" },
        { new SayTimeCommand { Time = 1000000L, EscapeDigits = "#" }, "SAY TIME 1000000 #" },
        { new SayTimeCommand { EscapeDigits = "#" }, "SAY TIME 0 #" },
        { new GetVariableCommand { Variable = "CALLERID(num)" }, "GET VARIABLE CALLERID(num)" },
        { new SetVariableCommand { Variable = "MYVAR", Value = "hello" }, "SET VARIABLE MYVAR hello" },
        { new SetContextCommand { Context = "default" }, "SET CONTEXT default" },
        { new SetExtensionCommand { Extension = "s" }, "SET EXTENSION s" },
        { new SetPriorityCommand { Priority = "1" }, "SET PRIORITY 1" },
        { new SetCallerIdCommand { CallerId = "1234567890" }, "SET CALLERID 1234567890" },
        { new SetAutoHangupCommand { Time = 60 }, "SET AUTOHANGUP 60" },
        { new SetAutoHangupCommand(), "SET AUTOHANGUP 0" },
        { new SendImageCommand { Image = "logo.png" }, "SEND IMAGE logo.png" },
        { new SendTextCommand { Text = "hello" }, "SEND TEXT \"hello\"" },
        { new SendTextCommand { Text = "say \"hi\"" }, "SEND TEXT \"say \\\"hi\\\"\"" },
        { new TddModeCommand { Mode = "on" }, "TDD MODE on" },
        { new WaitForDigitCommand { Timeout = 5000L }, "WAIT FOR DIGIT 5000" },
        { new WaitForDigitCommand(), "WAIT FOR DIGIT -1" },
        { new DatabaseGetCommand { Family = "cidname", Key = "1234" }, "DATABASE GET cidname 1234" },
        { new DatabasePutCommand { Family = "cidname", Key = "1234", Value = "John" }, "DATABASE PUT cidname 1234 John" },
        { new DatabaseDelCommand { Family = "cidname", KeyTree = "1234" }, "DATABASE DEL cidname 1234" },
        { new DatabaseDelTreeCommand { Family = "cidname" }, "DATABASE DELTREE cidname" },
        { new DatabaseDelTreeCommand { Family = "cidname", KeyTree = "sub" }, "DATABASE DELTREE cidname sub" },
        { new SpeechCreateCommand { Engine = "lumenvox" }, "SPEECH CREATE lumenvox" },
        { new SpeechActivateGrammarCommand { Name = "mygrammar" }, "SPEECH ACTIVATE GRAMMAR mygrammar" },
        { new SpeechDeactivateGrammarCommand { Name = "mygrammar" }, "SPEECH DEACTIVATE GRAMMAR mygrammar" },
        { new SpeechLoadGrammarCommand { Name = "mygrammar", Path = "/etc/grammar.xml" }, "SPEECH LOAD GRAMMAR mygrammar /etc/grammar.xml" },
        { new SpeechUnloadGrammarCommand { Name = "mygrammar" }, "SPEECH UNLOAD GRAMMAR mygrammar" },
        { new SpeechSetCommand { Name = "timeout", Value = "5" }, "SPEECH SET timeout 5" },

        // Commands with one optional param (testing both branches)
        { new HangupCommand(), "HANGUP" },
        { new HangupCommand { Channel = "SIP/100-0001" }, "HANGUP SIP/100-0001" },
        { new ChannelStatusCommand(), "CHANNEL STATUS" },
        { new ChannelStatusCommand { Channel = "SIP/100-0001" }, "CHANNEL STATUS SIP/100-0001" },
        { new SetMusicOnCommand(), "SET MUSIC ON" },
        { new SetMusicOnCommand { MusicOnHoldClass = "jazz" }, "SET MUSIC ON jazz" },
        { new ReceiveCharCommand(), "RECEIVE CHAR" },
        { new ReceiveCharCommand { Timeout = 5000 }, "RECEIVE CHAR 5000" },
        { new ReceiveTextCommand(), "RECEIVE TEXT" },
        { new ReceiveTextCommand { Timeout = 5000 }, "RECEIVE TEXT 5000" },
        { new GetFullVariableCommand { Variable = "${CDR(dst)}" }, "GET FULL VARIABLE ${CDR(dst)}" },
        { new GetFullVariableCommand { Variable = "${CDR(dst)}", Channel = "SIP/100" }, "GET FULL VARIABLE ${CDR(dst)} SIP/100" },
        { new ExecCommand { Application = "Playback" }, "EXEC Playback" },
        { new ExecCommand { Application = "Playback", Options = "hello-world" }, "EXEC Playback hello-world" },
        { new AgiCommand { Command = "googletts.agi" }, "AGI googletts.agi" },
        { new AgiCommand { Command = "googletts.agi", Args = "\"Hello World\"" }, "AGI googletts.agi \"Hello World\"" },
        { new BridgeCommand { Channel = "SIP/100" }, "EXEC Bridge SIP/100" },
        { new BridgeCommand { Channel = "SIP/100", Options = "phF" }, "EXEC Bridge SIP/100,phF" },
        { new QueueCommand { QueueName = "support" }, "EXEC Queue support" },
        { new QueueCommand { QueueName = "support", Options = "tT" }, "EXEC Queue support,tT" },
    };

    [Theory]
    [MemberData(nameof(SimpleCommandData))]
    public void BuildCommand_ShouldReturnExpectedString_WhenPropertiesAreSet(
        AgiCommandBase command, string expected)
    {
        command.BuildCommand().Should().Be(expected);
    }

    [Fact]
    public void AllCommands_ShouldInheritFromAgiCommandBase()
    {
        AgiCommandBase[] allCommands =
        [
            new AnswerCommand(),
            new AsyncAgiBreakCommand(),
            new AgiCommand(),
            new BridgeCommand(),
            new ChannelStatusCommand(),
            new ConfbridgeCommand(),
            new ControlStreamFileCommand(),
            new DatabaseDelCommand(),
            new DatabaseDelTreeCommand(),
            new DatabaseGetCommand(),
            new DatabasePutCommand(),
            new DialCommand(),
            new ExecCommand(),
            new GetDataCommand(),
            new GetFullVariableCommand(),
            new GetOptionCommand(),
            new GetVariableCommand(),
            new GosubCommand(),
            new HangupCommand(),
            new MeetmeCommand(),
            new NoopCommand(),
            new QueueCommand(),
            new ReceiveCharCommand(),
            new ReceiveTextCommand(),
            new RecordFileCommand(),
            new SayAlphaCommand(),
            new SayDateTimeCommand(),
            new SayDigitsCommand(),
            new SayNumberCommand(),
            new SayPhoneticCommand(),
            new SayTimeCommand(),
            new SendImageCommand(),
            new SendTextCommand(),
            new SetAutoHangupCommand(),
            new SetCallerIdCommand(),
            new SetContextCommand(),
            new SetExtensionCommand(),
            new SetMusicOffCommand(),
            new SetMusicOnCommand(),
            new SetPriorityCommand(),
            new SetVariableCommand(),
            new SpeechActivateGrammarCommand(),
            new SpeechCreateCommand(),
            new SpeechDeactivateGrammarCommand(),
            new SpeechDestroyCommand(),
            new SpeechLoadGrammarCommand(),
            new SpeechRecognizeCommand(),
            new SpeechSetCommand(),
            new SpeechUnloadGrammarCommand(),
            new StreamFileCommand(),
            new TddModeCommand(),
            new VerboseCommand(),
            new WaitForDigitCommand(),
        ];

        allCommands.Should().HaveCountGreaterThanOrEqualTo(48);

        foreach (var command in allCommands)
        {
            command.Should().BeAssignableTo<AgiCommandBase>();
        }
    }
}

#pragma warning restore CA1707
