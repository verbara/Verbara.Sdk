using Asterisk.Sdk;

namespace Asterisk.Sdk.Sessions.Exceptions;

public class SessionException(string message, Exception? inner = null)
    : AsteriskException(message, inner);

public class InvalidSessionStateTransitionException(CallSessionState from, CallSessionState to)
    : SessionException($"Invalid session state transition from {from} to {to}");
