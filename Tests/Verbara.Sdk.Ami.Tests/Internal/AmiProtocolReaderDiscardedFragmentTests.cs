using System.IO.Pipelines;
using System.Text;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// A message the end of the stream cut is counted when it is discarded, with what it was, so the connection can log it;
/// a stream that ends between messages discards nothing.
/// </summary>
public sealed class AmiProtocolReaderDiscardedFragmentTests
{
    [Theory]
    [InlineData("Event: Hangup\r\nChannel: PJSIP/t-1\r\n", "Hangup", 2)]
    [InlineData("Event: Hangup\r\nChannel: PJSIP/t-1\r\nUniqueid: 1.1\r\nCause: 1", "Hangup", 3)]
    [InlineData("Response: Success\r\nActionID: 7\r\n", "Response: Success", 2)]
    [InlineData("Event: Hang", null, 0)]
    public async Task ReadMessageAsync_ShouldCountTheDiscardedFragment_WhenTheStreamEndsInTheMiddleOfAMessage(
        string fragment, string? type, int fieldCount)
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(fragment));
        await pipe.Writer.CompleteAsync();

        var msg = await reader.ReadMessageAsync();

        using (new AssertionScope())
        {
            msg.Should().BeNull();
            reader.DiscardedFragments.Should().Be(1, "one message was cut by the end of the stream");
            reader.LastDiscardedFragmentType.Should().Be(type);
            reader.LastDiscardedFragmentFieldCount.Should().Be(fieldCount, "only the lines the stream completed are fields");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Event: Newstate\r\nChannel: PJSIP/a-1\r\n\r\n")]
    [InlineData("\r\n")]
    public async Task ReadMessageAsync_ShouldCountNoFragment_WhenTheStreamEndsBetweenMessages(string text)
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        await pipe.Writer.CompleteAsync();

        while (await reader.ReadMessageAsync() is not null)
        {
        }

        using (new AssertionScope())
        {
            reader.DiscardedFragments.Should().Be(0, "every message the stream carried was complete");
            reader.LastDiscardedFragmentType.Should().BeNull();
        }
    }
}
