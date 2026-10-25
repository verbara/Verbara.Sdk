using System.IO.Pipelines;
using System.Text;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// A stream that ends in the middle of a message carries no message: the fields read before the end are a fragment, not
/// an event or a response, and the reader returns <see langword="null"/> as it does for a stream that ends between
/// messages. A message the stream completed before it ended is still returned.
/// </summary>
public sealed class AmiProtocolReaderEndOfStreamTests
{
    [Theory]
    [InlineData("Event: Hangup\r\nChannel: PJSIP/t-1\r\n")]
    [InlineData("Event: Hangup\r\nChannel: PJSIP/t-1\r\nUniqueid: 1.1\r\nCause: 1")]
    [InlineData("Response: Success\r\nActionID: 7\r\n")]
    public async Task ReadMessageAsync_ShouldReturnNull_WhenTheStreamEndsInTheMiddleOfAMessage(string fragment)
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(fragment));
        await pipe.Writer.CompleteAsync();

        var msg = await reader.ReadMessageAsync();

        msg.Should().BeNull("the stream ended before the blank line that ends the message: what was read is a fragment");
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldReturnTheCompleteMessageAndThenNull_WhenTheStreamEndsInTheMiddleOfTheNextOne()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            "Event: Newstate\r\nChannel: PJSIP/a-1\r\n\r\nEvent: Hangup\r\nChannel: PJSIP/t-1\r\n"));
        await pipe.Writer.CompleteAsync();

        var first = await reader.ReadMessageAsync();
        var second = await reader.ReadMessageAsync();

        using (new AssertionScope())
        {
            first.Should().NotBeNull("the first message was complete before the stream ended");
            first?.EventType.Should().Be("Newstate");
            second.Should().BeNull("the second message was cut by the end of the stream");
        }
    }
}
