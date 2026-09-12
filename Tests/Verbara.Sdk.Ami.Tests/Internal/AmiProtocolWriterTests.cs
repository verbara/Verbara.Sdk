using System.Buffers;
using System.Collections;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;

namespace Verbara.Sdk.Ami.Tests.Internal;

public class AmiProtocolWriterTests
{
    [Fact]
    public async Task WriteActionAsync_ShouldWriteBasicAction()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteActionAsync("Ping", "1");

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Be("Action: Ping\r\nActionID: 1\r\n\r\n");
    }

    [Fact]
    public async Task WriteActionAsync_ShouldWriteActionWithFields()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteActionAsync("Originate", "42",
        [
            new("Channel", "SIP/2000"),
            new("Context", "default"),
            new("Exten", "1234"),
            new("Priority", "1")
        ]);

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Contain("Action: Originate\r\n");
        output.Should().Contain("ActionID: 42\r\n");
        output.Should().Contain("Channel: SIP/2000\r\n");
        output.Should().Contain("Context: default\r\n");
        output.Should().Contain("Exten: 1234\r\n");
        output.Should().Contain("Priority: 1\r\n");
        output.Should().EndWith("\r\n\r\n");
    }

    [Fact]
    public async Task WriteFieldsAsync_ShouldWriteRawFields()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteFieldsAsync(
        [
            new("Action", "Login"),
            new("Username", "admin"),
            new("Secret", "pass123")
        ]);

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Contain("Action: Login\r\n");
        output.Should().Contain("Username: admin\r\n");
        output.Should().Contain("Secret: pass123\r\n");
        output.Should().EndWith("\r\n\r\n");
    }

    [Fact]
    public async Task WriteActionAsync_ShouldHandleUtf8Characters()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteActionAsync("UserEvent", "99",
            [new("UserEvent", "TestEvent"), new("Data", "valor-especial")]);

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Contain("Data: valor-especial\r\n");
    }

    [Fact]
    public async Task RoundTrip_WriterThenReader_ShouldProduceParsableMessage()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var reader = new AmiProtocolReader(pipe.Reader);

        await writer.WriteActionAsync("QueueStatus", "77",
            [new("Queue", "sales")]);
        pipe.Writer.Complete();

        var msg = await reader.ReadMessageAsync();

        msg.Should().NotBeNull();
        msg!["Action"].Should().Be("QueueStatus");
        msg["ActionID"].Should().Be("77");
        msg["Queue"].Should().Be("sales");
    }

    // ── Line breaks inside an action ────────────────────────────────────────────
    // On the wire a CR or LF ends a header line early and an empty line ends the action, so a line
    // break would split one action into several. It is rejected before any byte of the action is
    // written, and the exception message never carries the rejected string.

    // Rejected strings are built from these runs, which no exception message template contains:
    // a three-character fragment of one of them in a message means the message carries the string.
    private const string Head = "7qz9xw";
    private const string Tail = "3jkvb5";
    private const string ValueToken = "5tpy8r";

    private const string NextAction = "Action: Ping\r\nActionID: next-1\r\n\r\n";

    private static readonly char[] LineBreakChars = ['\r', '\n'];

    private static string WithLineBreak(string lineBreak) => Head + lineBreak + Tail;

    private static KeyValuePair<string, string>[] ThreeFields() =>
    [
        new("Channel", "PJSIP/2000"),
        new("Context", "default"),
        new("Exten", "100"),
    ];

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task WriteActionAsync_ShouldThrowArgumentException_WhenActionNameContainsLineBreak(string lineBreak)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteActionAsync(WithLineBreak(lineBreak), "1", ThreeFields());

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("actionName");
        thrown.Message.Should().Contain("action name");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task WriteActionAsync_ShouldThrowArgumentException_WhenActionIdContainsLineBreak(string lineBreak)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteActionAsync("Originate", WithLineBreak(lineBreak), ThreeFields());

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("actionId");
        thrown.Message.Should().Contain("ActionID");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Theory]
    [InlineData("\r", 0)]
    [InlineData("\r", 1)]
    [InlineData("\r", 2)]
    [InlineData("\n", 0)]
    [InlineData("\n", 1)]
    [InlineData("\n", 2)]
    [InlineData("\r\n", 0)]
    [InlineData("\r\n", 1)]
    [InlineData("\r\n", 2)]
    public async Task WriteActionAsync_ShouldThrowArgumentException_WhenFieldKeyContainsLineBreak(string lineBreak, int index)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var fields = ThreeFields();
        fields[index] = new(WithLineBreak(lineBreak), ValueToken);

        var act = async () => await writer.WriteActionAsync("Originate", "1", fields);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("fields");
        thrown.Message.Should().Contain(string.Create(CultureInfo.InvariantCulture, $"index {index}"));
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail, ValueToken);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Theory]
    [InlineData("\r", 0)]
    [InlineData("\r", 1)]
    [InlineData("\r", 2)]
    [InlineData("\n", 0)]
    [InlineData("\n", 1)]
    [InlineData("\n", 2)]
    [InlineData("\r\n", 0)]
    [InlineData("\r\n", 1)]
    [InlineData("\r\n", 2)]
    public async Task WriteActionAsync_ShouldThrowArgumentException_WhenFieldValueContainsLineBreak(string lineBreak, int index)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var fields = ThreeFields();
        var key = fields[index].Key;
        fields[index] = new(key, WithLineBreak(lineBreak));

        var act = async () => await writer.WriteActionAsync("Originate", "1", fields);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("fields");
        thrown.Message.Should().Contain("'" + key + "'");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task WriteActionAsync_ShouldThrowArgumentException_WhenLazyFieldsYieldLineBreakInLastValue(string lineBreak)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteActionAsync("Originate", "1", LazyFields(WithLineBreak(lineBreak)));

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.Message.Should().Contain("'Exten'");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(39)]
    public async Task WriteActionAsync_ShouldThrowArgumentException_WhenLineBreakFollowsManyValidFields(int index)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteActionAsync("UpdateConfig", "1", ManyFields(40, lineBreakAt: index));

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.Message.Should().Contain("'Var-" + index.ToString("D6", CultureInfo.InvariantCulture) + "'");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Fact]
    public async Task WriteActionAsync_ShouldWriteAllFieldsInOrder_WhenActionHasManyFields()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteActionAsync("UpdateConfig", "1", ManyFields(40, lineBreakAt: -1));
        await pipe.Writer.CompleteAsync();

        var expected = new StringBuilder("Action: UpdateConfig\r\nActionID: 1\r\n");
        foreach (var field in ManyFields(40, lineBreakAt: -1))
            expected.Append(field.Key).Append(": ").Append(field.Value).Append("\r\n");
        expected.Append("\r\n");

        Encoding.UTF8.GetString(await ReadAllAsync(pipe.Reader)).Should().Be(expected.ToString());
    }

    [Fact]
    public async Task WriteActionAsync_ShouldWriteTheCheckedStrings_WhenFieldsYieldDifferentValuesOnLaterEnumerations()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var fields = new ChangingFields();

        await writer.WriteActionAsync("Originate", "1", fields);
        await pipe.Writer.CompleteAsync();

        fields.Enumerations.Should().Be(1, "the strings written must be the strings that were checked");
        Encoding.UTF8.GetString(await ReadAllAsync(pipe.Reader))
            .Should().Be("Action: Originate\r\nActionID: 1\r\nChannel: PJSIP/2000\r\n\r\n");
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task WriteFieldsAsync_ShouldThrowArgumentException_WhenFieldValueContainsLineBreak(string lineBreak)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteFieldsAsync(
        [
            new("Action", "Login"),
            new("Username", "admin"),
            new("Secret", WithLineBreak(lineBreak)),
        ]);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("fields");
        thrown.Message.Should().Contain("'Secret'");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task WriteFieldsAsync_ShouldThrowArgumentException_WhenFieldKeyContainsLineBreak(string lineBreak)
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteFieldsAsync(
        [
            new("Action", "Login"),
            new(WithLineBreak(lineBreak), ValueToken),
        ]);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.ParamName.Should().Be("fields");
        thrown.Message.Should().Contain("index 1");
        ShouldCarryNoFragmentOf(thrown.Message, Head, Tail, ValueToken);
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Fact]
    public async Task WriteActionAsync_ShouldWriteUtf8BytesUnchanged_WhenStringsContainOtherControlCharactersOrUnicode()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        // Built from code points so the source stays ASCII. U+0085, U+2028 and U+2029 are line
        // separators to Unicode, but their UTF-8 bytes contain no 0x0A or 0x0D.
        string[] values =
        [
            "tab\there",
            "nul\0byte",
            "vt" + Cp(0x0B) + "ff" + Cp(0x0C),
            "esc" + Cp(0x1B) + "[0m",
            "del" + Cp(0x7F),
            "nel" + Cp(0x85) + "end",
            "ls" + Cp(0x2028) + "ps" + Cp(0x2029) + "end",
            "a: b: c",
            "\"quoted\"",
            "back\\slash",
            "h" + Cp(0xE9) + "llo " + Cp(0x4E2D) + Cp(0x6587) + " " + Cp(0x1F389),
            "",
            " padded ",
        ];
        var fields = new KeyValuePair<string, string>[values.Length + 1];
        for (var i = 0; i < values.Length; i++)
            fields[i] = new(string.Create(CultureInfo.InvariantCulture, $"Field{i}"), values[i]);
        fields[^1] = new("Cl" + Cp(0xE9) + Cp(0x2028) + Cp(0x85), "key with Unicode");

        var actionName = "UserEvent" + Cp(0x85);
        var actionId = "id:" + Cp(0x2029) + "\t1";

        await writer.WriteActionAsync(actionName, actionId, fields);
        await pipe.Writer.CompleteAsync();

        var expected = new StringBuilder()
            .Append("Action: ").Append(actionName).Append("\r\n")
            .Append("ActionID: ").Append(actionId).Append("\r\n");
        foreach (var field in fields)
            expected.Append(field.Key).Append(": ").Append(field.Value).Append("\r\n");
        expected.Append("\r\n");

        (await ReadAllAsync(pipe.Reader)).Should().Equal(Encoding.UTF8.GetBytes(expected.ToString()));
    }

    // ── Null arguments: rejected before any byte is written ─────────────────────

    [Fact]
    public async Task WriteActionAsync_ShouldThrowArgumentNullException_WhenActionNameIsNull()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteActionAsync(null!, "1", ThreeFields());

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("actionName");
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Fact]
    public async Task WriteActionAsync_ShouldThrowArgumentNullException_WhenActionIdIsNull()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteActionAsync("Originate", null!, ThreeFields());

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("actionId");
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Fact]
    public async Task WriteFieldsAsync_ShouldThrowArgumentNullException_WhenFieldsIsNull()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        var act = async () => await writer.WriteFieldsAsync(null!);

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("fields");
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    [Fact]
    public async Task WriteActionAsync_ShouldThrowArgumentNullException_WhenLastFieldValueIsNull()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var fields = ThreeFields();
        fields[2] = new("Exten", null!);

        var act = async () => await writer.WriteActionAsync("Originate", "1", fields);

        await act.Should().ThrowAsync<ArgumentNullException>();
        await ShouldHoldNoByteThenWriteNextActionExactlyAsync(pipe, writer);
    }

    private static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static async Task ShouldHoldNoByteThenWriteNextActionExactlyAsync(Pipe pipe, AmiProtocolWriter writer)
    {
        pipe.Writer.UnflushedBytes.Should().Be(0, "no byte of a rejected action may reach the PipeWriter");
        pipe.Reader.TryRead(out _).Should().BeFalse("no byte of a rejected action may become readable");

        await writer.WriteActionAsync("Ping", "next-1");
        await pipe.Writer.CompleteAsync();

        Encoding.UTF8.GetString(await ReadAllAsync(pipe.Reader)).Should().Be(NextAction);
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
                        "an exception message must not carry any part of a rejected string");
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

    private static IEnumerable<KeyValuePair<string, string>> LazyFields(string lastValue)
    {
        yield return new("Channel", "PJSIP/2000");
        yield return new("Context", "default");
        yield return new("Exten", lastValue);
    }

    private static IEnumerable<KeyValuePair<string, string>> ManyFields(int count, int lineBreakAt)
    {
        for (var i = 0; i < count; i++)
        {
            var number = i.ToString("D6", CultureInfo.InvariantCulture);
            yield return new("Var-" + number, i == lineBreakAt ? WithLineBreak("\r\n") : "value-" + number);
        }
    }

    /// <summary>Yields a valid field on its first enumeration and a line break on every later one.</summary>
    private sealed class ChangingFields : IEnumerable<KeyValuePair<string, string>>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            Enumerations++;
            IEnumerable<KeyValuePair<string, string>> items =
                [new("Channel", Enumerations == 1 ? "PJSIP/2000" : WithLineBreak("\r\n"))];
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
