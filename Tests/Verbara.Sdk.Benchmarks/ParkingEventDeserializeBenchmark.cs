using System.IO.Pipelines;
using System.Text;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Benchmarks the source-generated event deserializer on two frames captured from Asterisk 22.11.0 verbatim:
/// a <c>Newchannel</c> (inherited channel members, no prefix) and a <c>ParkedCall</c> (the parked channel's members
/// carried under the <c>Parkee</c> prefix). The frames are parsed once in setup, so a row measures only the
/// <c>AmiMessage</c> to <c>ManagerEvent</c> step. The file depends on nothing newer than the deserializer itself, so it
/// builds unchanged against an older tag and the two sides of a comparison read the same bytes.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ParkingEventDeserializeBenchmark
{
    private const string NewchannelFrame =
        "Event: Newchannel\r\n" +
        "Privilege: call,all\r\n" +
        "Timestamp: 1793487744.045460\r\n" +
        "Channel: Local/callerid@capture-00000000;1\r\n" +
        "ChannelState: 0\r\n" +
        "ChannelStateDesc: Down\r\n" +
        "CallerIDNum: <unknown>\r\n" +
        "CallerIDName: <unknown>\r\n" +
        "ConnectedLineNum: <unknown>\r\n" +
        "ConnectedLineName: <unknown>\r\n" +
        "Language: en\r\n" +
        "AccountCode: \r\n" +
        "Context: capture\r\n" +
        "Exten: callerid\r\n" +
        "Priority: 1\r\n" +
        "Uniqueid: 1793487744.0\r\n" +
        "Linkedid: 1793487744.0\r\n\r\n";

    private const string ParkedCallFrame =
        "Event: ParkedCall\r\n" +
        "Privilege: call,all\r\n" +
        "Timestamp: 1793487750.560945\r\n" +
        "ParkeeChannel: Local/park@capture-00000002;2\r\n" +
        "ParkeeChannelState: 6\r\n" +
        "ParkeeChannelStateDesc: Up\r\n" +
        "ParkeeCallerIDNum: 7102\r\n" +
        "ParkeeCallerIDName: Capture\r\n" +
        "ParkeeConnectedLineNum: 7102\r\n" +
        "ParkeeConnectedLineName: Capture\r\n" +
        "ParkeeLanguage: en\r\n" +
        "ParkeeAccountCode: \r\n" +
        "ParkeeContext: capture\r\n" +
        "ParkeeExten: park\r\n" +
        "ParkeePriority: 2\r\n" +
        "ParkeeUniqueid: 1793487750.5\r\n" +
        "ParkeeLinkedid: 1793487750.4\r\n" +
        "ParkerDialString: Local/park@capture\r\n" +
        "Parkinglot: default\r\n" +
        "ParkingSpace: 701\r\n" +
        "ParkingTimeout: 20\r\n" +
        "ParkingDuration: 0\r\n\r\n";

    private AmiMessage _newchannel = null!;
    private AmiMessage _parkedCall = null!;

    [GlobalSetup]
    public void Setup()
    {
        _newchannel = Parse(NewchannelFrame);
        _parkedCall = Parse(ParkedCallFrame);
    }

    private static AmiMessage Parse(string frame)
    {
        var pipe = new Pipe();
        pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(frame)).AsTask().GetAwaiter().GetResult();
        pipe.Writer.Complete();
        var reader = new AmiProtocolReader(pipe.Reader);
        var message = reader.ReadMessageAsync().AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The captured frame did not parse.");
        pipe.Reader.Complete();
        return message;
    }

    [Benchmark(Baseline = true)]
    public ManagerEvent DeserializeNewchannel() => GeneratedEventDeserializer.Deserialize(_newchannel);

    [Benchmark]
    public ManagerEvent DeserializeParkedCall() => GeneratedEventDeserializer.Deserialize(_parkedCall);
}
