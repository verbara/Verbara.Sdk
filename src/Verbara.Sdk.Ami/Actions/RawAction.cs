using System.Collections.ObjectModel;
using Verbara.Sdk;

namespace Verbara.Sdk.Ami.Actions;

/// <summary>
/// An AMI action sent by the name and the fields its caller gives: the route for an action the SDK does not type,
/// such as <c>LoggerRotate</c>, <c>IAXpeers</c>, <c>ListCategories</c> or a <c>DAHDI*</c> action.
/// </summary>
/// <remarks>
/// <para>
/// Every send method of <see cref="IAmiConnection"/> accepts it. On the wire it is <c>Action: </c> and the name,
/// the connection's <c>ActionID</c>, then each field in the order it was added (a repeated key is written each time),
/// then the blank line:
/// </para>
/// <code>
/// var response = await connection.SendActionAsync(
///     new RawAction("DAHDIDNDon").Add("DAHDIChannel", "1"), cancellationToken);
/// </code>
/// <para>
/// The name and every field are checked where the action is built, so a refused one throws
/// <see cref="ArgumentException"/> from the constructor or from <see cref="Add"/>, before any connection is touched:
/// the name is ASCII letters and digits; a key is ASCII letters, digits, <c>-</c>, <c>_</c>, <c>(</c> or <c>)</c>,
/// and is not <c>Action</c> or <c>ActionID</c> in any letter case; a value holds no CR or LF. These are the SDK's
/// rules, chosen so every written line is one key and one value; they are not a list of what Asterisk registers.
/// An exception never includes a value, which may be a secret. The connection's <c>Trace</c> log still writes every
/// sent field with its value, as it does for every action.
/// </para>
/// <para>
/// The response is read by the action's name, in any letter case: a raw <c>Ping</c> is answered with the same response
/// type as the typed ping action, and an untyped name with a base <see cref="ManagerResponse"/> whose
/// <see cref="ManagerResponse.RawFields"/> hold every header Asterisk sent. A name Asterisk does not register is
/// written and answered by Asterisk with <c>Response: Error</c>. Like every action, an instance is owned by its caller
/// and is not safe to change while it is being sent.
/// </para>
/// </remarks>
public sealed class RawAction : ManagerAction
{
    private readonly List<KeyValuePair<string, string>> _fields = [];

    /// <summary>Creates a raw action with the given name and no fields.</summary>
    /// <param name="name">The AMI action name, written exactly as given (letter case included).</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is <see langword="null"/>, empty, or holds a character other than the ASCII letters and
    /// digits.
    /// </exception>
    public RawAction(string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name), "A raw action needs a name.");
        if (name.Length == 0 || !IsAsciiLettersAndDigits(name))
            throw new ArgumentException(
                "A raw action name is ASCII letters and digits only, as every action Asterisk registers is.", nameof(name));

        Name = name;
        Fields = new ReadOnlyCollection<KeyValuePair<string, string>>(_fields);
    }

    /// <summary>The AMI action name, exactly as given.</summary>
    public string Name { get; }

    /// <summary>The fields in the order they were added, repeated keys included.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

    /// <summary>Appends a field; a key added twice is written twice, in order.</summary>
    /// <param name="key">The header key.</param>
    /// <param name="value">The header value, possibly empty.</param>
    /// <returns>This action, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty, holds a character other than ASCII letters, digits,
    /// <c>-</c>, <c>_</c>, <c>(</c> or <c>)</c>, or is <c>Action</c> or <c>ActionID</c> in any letter case; or
    /// <paramref name="value"/> is <see langword="null"/> or holds CR or LF. The fields are unchanged, and the message
    /// never includes the value.
    /// </exception>
    public RawAction Add(string key, string value)
    {
        if (key is null)
            throw new ArgumentNullException(nameof(key), "A raw action field needs a key.");
        if (key.Length == 0 || !IsKey(key))
            throw new ArgumentException(
                "A raw action field key is ASCII letters, digits, '-', '_', '(' or ')' only.", nameof(key));
        if (string.Equals(key, "Action", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "ActionID", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "A raw action field key cannot be 'Action' or 'ActionID': the name and the ActionID are written by the connection.",
                nameof(key));
        if (value is null)
            throw new ArgumentNullException(nameof(value), $"The value of the raw action field '{key}' is null.");
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException(
                $"The value of the raw action field '{key}' holds a line break (CR or LF).", nameof(value));

        _fields.Add(new KeyValuePair<string, string>(key, value));
        return this;
    }

    private static bool IsAsciiLettersAndDigits(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }

        return true;
    }

    private static bool IsKey(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '(' or ')'))
                return false;
        }

        return true;
    }
}
