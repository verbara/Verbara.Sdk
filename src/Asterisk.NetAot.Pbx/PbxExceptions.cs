namespace Asterisk.NetAot.Pbx;

public class PbxException(string message, Exception? inner = null)
    : Exception(message, inner);

public class InvalidChannelNameException(string channelName)
    : PbxException($"Invalid channel name: {channelName}");

public class ActivityFailedException(string activityName, string reason)
    : PbxException($"Activity '{activityName}' failed: {reason}");
