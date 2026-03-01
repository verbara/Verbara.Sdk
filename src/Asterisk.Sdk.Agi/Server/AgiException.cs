namespace Asterisk.Sdk.Agi.Server;

public class AgiException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public class AgiHangupException(string message = "Channel hung up")
    : AgiException(message);

public class AgiNetworkException(string message, Exception? innerException = null)
    : AgiException(message, innerException);
