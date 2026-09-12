using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Verbara.Sdk.Agi.Server;
using FluentAssertions;

namespace Verbara.Sdk.Agi.Tests.Server;

/// <summary>
/// An AGI command is one line on the wire, so a CR or LF inside it would split one command into
/// several. The writer rejects it before any byte is written, and the message never carries the
/// command text.
/// </summary>
public class FastAgiWriterTests
{
    // Rejected commands are built from these runs, which the message template does not contain:
    // a three-character fragment of one of them in a message means the message carries the command.
    private const string Head = "7qz9xw";
    private const string Tail = "3jkvb5";

    private static readonly char[] LineBreakChars = ['\r', '\n'];

    [Theory]
    [InlineData("\r", "start")]
    [InlineData("\r", "middle")]
    [InlineData("\r", "end")]
    [InlineData("\n", "start")]
    [InlineData("\n", "middle")]
    [InlineData("\n", "end")]
    [InlineData("\r\n", "start")]
    [InlineData("\r\n", "middle")]
    [InlineData("\r\n", "end")]
    public async Task SendCommandAsync_ShouldThrowArgumentException_WhenCommandContainsLineBreak(string lineBreak, string position)
    {
        var pipe = new Pipe();
        var writer = new FastAgiWriter(pipe.Writer);
        var command = position switch
        {
            "start" => lineBreak + "SAY ALPHA " + Head + " " + Tail,
            "middle" => "SAY ALPHA " + Head + lineBreak + Tail + " #",
            _ => "SAY ALPHA " + Head + " " + Tail + lineBreak,
        };

        var act = async () => await writer.SendCommandAsync(command);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("command");
        thrown.Message.Should().NotContain("SAY ALPHA");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail);
        await ShouldHoldNoByteThenSendNextCommandExactlyAsync(pipe, writer);
    }

    [Fact]
    public async Task SendCommandAsync_ShouldWriteUtf8BytesUnchanged_WhenCommandContainsOtherControlCharactersOrUnicode()
    {
        var pipe = new Pipe();
        var writer = new FastAgiWriter(pipe.Writer);

        // Built from code points so the source stays ASCII. U+0085, U+2028 and U+2029 are line
        // separators to Unicode, but their UTF-8 bytes contain no 0x0A or 0x0D.
        var command =
            "SET VARIABLE v \"tab\there nul\0 vt" + Cp(0x0B) + " ff" + Cp(0x0C) + " esc" + Cp(0x1B)
            + " del" + Cp(0x7F) + " nel" + Cp(0x85) + " ls" + Cp(0x2028) + " ps" + Cp(0x2029)
            + " a:b \\\" h" + Cp(0xE9) + "llo " + Cp(0x4E2D) + Cp(0x6587) + " " + Cp(0x1F389) + "\"";

        await writer.SendCommandAsync(command);
        await pipe.Writer.CompleteAsync();

        (await ReadAllAsync(pipe.Reader)).Should().Equal(Encoding.UTF8.GetBytes(command + "\n"));
    }

    private static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static async Task ShouldHoldNoByteThenSendNextCommandExactlyAsync(Pipe pipe, FastAgiWriter writer)
    {
        pipe.Writer.UnflushedBytes.Should().Be(0, "no byte of a rejected command may reach the PipeWriter");
        pipe.Reader.TryRead(out _).Should().BeFalse("no byte of a rejected command may become readable");

        await writer.SendCommandAsync("ANSWER");
        await pipe.Writer.CompleteAsync();

        Encoding.UTF8.GetString(await ReadAllAsync(pipe.Reader)).Should().Be("ANSWER\n");
    }

    private static void ShouldCarryNoFragmentOf(string message, params string[] rejected)
    {
        foreach (var text in rejected)
        {
            foreach (var segment in text.Split(LineBreakChars, StringSplitOptions.RemoveEmptyEntries))
            {
                for (var i = 0; i + 3 <= segment.Length; i++)
                {
                    message.Should().NotContain(segment.Substring(i, 3),
                        "an exception message must not carry any part of a rejected command");
                }
            }
        }
    }

    private static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        while (true)
        {
            var result = await reader.ReadAsync();
            if (result.IsCompleted)
            {
                var bytes = result.Buffer.ToArray();
                reader.AdvanceTo(result.Buffer.End);
                return bytes;
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }
}
