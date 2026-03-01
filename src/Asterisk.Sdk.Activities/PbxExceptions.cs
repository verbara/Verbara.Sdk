using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities;

public class ActivityException(string message, Exception? inner = null)
    : AsteriskException(message, inner);

public class InvalidChannelNameException(string channelName)
    : ActivityException($"Invalid channel name: {channelName}");

public class ActivityFailedException(string activityName, string reason)
    : ActivityException($"Activity '{activityName}' failed: {reason}");
