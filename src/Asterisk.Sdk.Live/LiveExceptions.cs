using Asterisk.Sdk;

namespace Asterisk.Sdk.Live;

public class LiveException(string message, Exception? inner = null)
    : AsteriskException(message, inner);

public class AmiCommunicationException(string message, Exception? inner = null)
    : LiveException(message, inner);

public class ChannelNotFoundException(string channelName)
    : LiveException($"Channel not found: {channelName}");

public class InterfaceNotFoundException(string interfaceName)
    : LiveException($"Interface not found: {interfaceName}");

public class InvalidPenaltyException(int penalty)
    : LiveException($"Invalid penalty value: {penalty}");
