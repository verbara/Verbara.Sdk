using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

namespace Verbara.Sdk.Ami.Internal;

/// <summary>
/// Serializes AMI actions to the wire protocol format using PipeWriter.
/// Format:
///   Action: ActionName\r\n
///   ActionID: id\r\n
///   Key: Value\r\n
///   \r\n
/// </summary>
/// <remarks>
/// A line break (CR or LF) inside the action name, the ActionID, a field key or a field value is
/// rejected with <see cref="ArgumentException"/>: on the wire it would end the header early, and a
/// blank line would end the action, so one action would be split into several. Every string of an
/// action is checked before the first byte of that action is requested from the
/// <see cref="PipeWriter"/>, so a rejected action leaves nothing behind in it. Like the
/// <see cref="PipeWriter"/> it wraps, an instance is not safe for concurrent use.
/// </remarks>
public sealed class AmiProtocolWriter
{
    private const string ActionKey = "Action";
    private const string ActionIdKey = "ActionID";

    private static ReadOnlySpan<byte> CrLf => "\r\n"u8;
    private static ReadOnlySpan<byte> ColonSpace => ": "u8;

    private readonly PipeWriter _writer;

    public AmiProtocolWriter(PipeWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Write an AMI action as key-value text followed by a blank line terminator.
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="actionName"/>, <paramref name="actionId"/>, or a key or value in
    /// <paramref name="fields"/> contains a line break (CR or LF). Nothing of the action is written.
    /// The message names the field but never includes its value.
    /// </exception>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="actionName"/> or <paramref name="actionId"/> is <see langword="null"/>, or a key
    /// or value in <paramref name="fields"/> is. Nothing of the action is written.
    /// </exception>
    public async ValueTask WriteActionAsync(string actionName, string actionId,
        IEnumerable<KeyValuePair<string, string>>? fields = null,
        CancellationToken cancellationToken = default)
    {
        // WriteMessage reads a null name or ID as "no such header", so null must stop here.
        ArgumentNullException.ThrowIfNull(actionName);
        ArgumentNullException.ThrowIfNull(actionId);

        WriteMessage(actionName, actionId, fields);
        await _writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Write a raw dictionary of fields (used by source-generated serializers).
    /// The caller is responsible for including Action and ActionID fields.
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// A key or value in <paramref name="fields"/> contains a line break (CR or LF). Nothing of the
    /// action is written. The message names the field but never includes its value.
    /// </exception>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="fields"/> is <see langword="null"/>, or a key or value in it is. Nothing of the
    /// action is written.
    /// </exception>
    public async ValueTask WriteFieldsAsync(IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken = default)
    {
        // WriteMessage reads null fields as "no fields", which would write a lone blank line.
        ArgumentNullException.ThrowIfNull(fields);

        WriteMessage(null, null, fields);
        await _writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Validates and measures the whole action first — enumerating <paramref name="fields"/> exactly
    /// once and keeping the pairs it yields — and only then encodes it into a single span of the
    /// <see cref="PipeWriter"/>. The strings written are the strings that were checked, even when
    /// <paramref name="fields"/> is a lazy iterator that would yield different ones a second time.
    /// </summary>
    private void WriteMessage(string? actionName, string? actionId,
        IEnumerable<KeyValuePair<string, string>>? fields)
    {
        var inline = default(InlineFields);
        Span<KeyValuePair<string, string>> buffer = inline;
        KeyValuePair<string, string>[]? rented = null;

        try
        {
            // Phase 1: validate and measure. Nothing touches the PipeWriter here.
            var total = CrLf.Length; // blank line terminator

            if (actionName is not null)
            {
                if (ContainsLineBreak(actionName))
                    ThrowLineBreak(LineBreakInActionName, nameof(actionName));
                total = checked(total + MeasureField(ActionKey, actionName));
            }

            if (actionId is not null)
            {
                if (ContainsLineBreak(actionId))
                    ThrowLineBreak(LineBreakInActionId, nameof(actionId));
                total = checked(total + MeasureField(ActionIdKey, actionId));
            }

            var count = 0;
            if (fields is not null)
            {
                foreach (var field in fields)
                {
                    if (ContainsLineBreak(field.Key))
                        ThrowLineBreakInFieldKey(count, nameof(fields));
                    if (ContainsLineBreak(field.Value))
                        ThrowLineBreakInFieldValue(field.Key, nameof(fields));

                    total = checked(total + MeasureField(field.Key, field.Value));

                    if (count == buffer.Length)
                        buffer = Grow(buffer, ref rented);
                    buffer[count++] = field;
                }
            }

            // Phase 2: the action is valid in full; encode it into one span and commit it once.
            var span = _writer.GetSpan(total);
            var written = 0;

            if (actionName is not null)
                written += EncodeField(span[written..], ActionKey, actionName);
            if (actionId is not null)
                written += EncodeField(span[written..], ActionIdKey, actionId);

            foreach (var field in buffer[..count])
                written += EncodeField(span[written..], field.Key, field.Value);

            CrLf.CopyTo(span[written..]);
            written += CrLf.Length;

            _writer.Advance(written);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<KeyValuePair<string, string>>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool ContainsLineBreak(string value) => value.AsSpan().ContainsAny('\r', '\n');

    private static int MeasureField(string key, string value) =>
        checked(Encoding.UTF8.GetByteCount(key) + ColonSpace.Length
            + Encoding.UTF8.GetByteCount(value) + CrLf.Length);

    private static int EncodeField(Span<byte> destination, string key, string value)
    {
        var written = Encoding.UTF8.GetBytes(key, destination);
        ColonSpace.CopyTo(destination[written..]);
        written += ColonSpace.Length;
        written += Encoding.UTF8.GetBytes(value, destination[written..]);
        CrLf.CopyTo(destination[written..]);
        return written + CrLf.Length;
    }

    /// <summary>
    /// Moves the kept pairs to a pooled array twice the size. Only actions with more fields than
    /// <see cref="InlineFields"/> holds get here.
    /// </summary>
    private static Span<KeyValuePair<string, string>> Grow(
        Span<KeyValuePair<string, string>> current, ref KeyValuePair<string, string>[]? rented)
    {
        var larger = ArrayPool<KeyValuePair<string, string>>.Shared.Rent(current.Length * 2);
        current.CopyTo(larger);

        if (rented is not null)
            ArrayPool<KeyValuePair<string, string>>.Shared.Return(rented, clearArray: true);

        rented = larger;
        return larger;
    }

    private const string LineBreakInActionName =
        "The AMI action name contains a line break (CR or LF), which would split one action into several on the wire.";

    private const string LineBreakInActionId =
        "The AMI ActionID contains a line break (CR or LF), which would split one action into several on the wire.";

    [DoesNotReturn]
    private static void ThrowLineBreak(string message, string paramName) =>
        throw new ArgumentException(message, paramName);

    // The key itself is the offending string here, so the field is identified by its position.
    [DoesNotReturn]
    private static void ThrowLineBreakInFieldKey(int index, string paramName) =>
        throw new ArgumentException(
            string.Create(CultureInfo.InvariantCulture,
                $"The key of the AMI field at index {index} contains a line break (CR or LF), which would split one action into several on the wire."),
            paramName);

    // Only the key is named: field values can be secrets.
    [DoesNotReturn]
    private static void ThrowLineBreakInFieldValue(string key, string paramName) =>
        throw new ArgumentException(
            $"The value of AMI field '{key}' contains a line break (CR or LF), which would split one action into several on the wire. The value is not shown.",
            paramName);

    /// <summary>Stack storage for the pairs of a typical action, so the common case rents nothing.</summary>
    [InlineArray(16)]
    private struct InlineFields
    {
        private KeyValuePair<string, string> _element0;
    }
}
