using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari;

/// <summary>Base exception for ARI-related errors.</summary>
public class AriException(string message, int? statusCode = null, Exception? innerException = null)
    : AsteriskException(message, innerException)
{
    /// <summary>HTTP status code from the ARI response, if available.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Thrown when an ARI resource is not found (HTTP 404).</summary>
public class AriNotFoundException(string message, Exception? innerException = null)
    : AriException(message, 404, innerException);

/// <summary>Thrown when an ARI operation conflicts with current state (HTTP 409).</summary>
public class AriConflictException(string message, Exception? innerException = null)
    : AriException(message, 409, innerException);
