using Asterisk.Sdk;

namespace Asterisk.Sdk.Ami;

/// <summary>Base exception for AMI-related errors.</summary>
public class AmiException(string message, Exception? innerException = null)
    : AsteriskException(message, innerException);

/// <summary>Thrown when AMI authentication fails (wrong credentials or challenge failure).</summary>
public class AmiAuthenticationException(string message, Exception? innerException = null)
    : AmiException(message, innerException);

/// <summary>Thrown when an AMI connection cannot be established or is lost.</summary>
public class AmiConnectionException(string message, Exception? innerException = null)
    : AmiException(message, innerException);

/// <summary>Thrown when the AMI protocol response is malformed or unexpected.</summary>
public class AmiProtocolException(string message, Exception? innerException = null)
    : AmiException(message, innerException);

/// <summary>Thrown when an AMI action times out waiting for a response.</summary>
public class AmiTimeoutException(string message, Exception? innerException = null)
    : AmiException(message, innerException);

/// <summary>Thrown when an operation requires an active AMI connection but none exists.</summary>
public class AmiNotConnectedException(string message = "Not connected to Asterisk AMI")
    : AmiException(message);
