using Asterisk.Sdk;

namespace Asterisk.Sdk.Agi.Server;

public class AgiException(string message, Exception? innerException = null)
    : AsteriskException(message, innerException);

public class AgiHangupException(string message = "Channel hung up")
    : AgiException(message);

public class AgiNetworkException(string message, Exception? innerException = null)
    : AgiException(message, innerException);
