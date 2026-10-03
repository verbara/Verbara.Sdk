using System.Globalization;

namespace Verbara.Sdk.VoiceAi.Tts.Internal;

/// <summary>
/// The range a provider's timeout in whole seconds accepts — 1 to 600, the same as the options' <c>[Range(1, 600)]</c>
/// — checked where the options validator does not reach: a client built from <c>Options.Create</c> or a hand-made
/// registration, and a value changed on the options object after the client was built (clients hold that object by
/// reference). Before the range, <c>0</c> failed every call as a handshake failure and <c>-1</c> threw from inside a
/// timer, naming no option.
/// </summary>
internal static class TimeoutSecondsRule
{
    /// <summary>The smallest accepted value, in seconds.</summary>
    internal const int Minimum = 1;

    /// <summary>The largest accepted value, in seconds.</summary>
    internal const int Maximum = 600;

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> whose <c>ParamName</c> is <paramref name="option"/> when
    /// <paramref name="seconds"/> is outside 1–600; otherwise returns it as a <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="seconds">The configured value.</param>
    /// <param name="option">The option's name, as a host sets it: <c>ConnectTimeoutSeconds</c> by default.</param>
    internal static TimeSpan ToLimit(int seconds, string option = "ConnectTimeoutSeconds")
    {
        if (seconds is < Minimum or > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                option,
                seconds,
                string.Create(CultureInfo.InvariantCulture, $"{option} must be between {Minimum} and {Maximum} seconds; it was {seconds}."));
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
