namespace Verbara.Sdk;

/// <summary>
/// Base exception for all Verbara Sdk errors.
/// </summary>
public class AsteriskException(string message, Exception? innerException = null)
    : Exception(message, innerException);
