namespace Asterisk.Sdk;

/// <summary>
/// Base exception for all Asterisk SDK errors.
/// </summary>
public class AsteriskException(string message, Exception? innerException = null)
    : Exception(message, innerException);
