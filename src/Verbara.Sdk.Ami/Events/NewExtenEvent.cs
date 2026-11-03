using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

/// <summary>
/// A channel reached a dialplan step (<c>Newexten</c>, class <c>dialplan</c>). <see cref="Context"/>,
/// <see cref="Exten"/> and <see cref="Priority"/> name the step under the names and types every channel event uses;
/// <see cref="Extension"/> carries the header of that name, which holds the same value.
/// </summary>
[VerbaraMapping("NewExten")]
public sealed class NewExtenEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? Application { get; set; }
    public string? AppData { get; set; }
    public string? Channel { get; set; }

    /// <summary>The <c>Extension</c> header. Asterisk sends the same value in <c>Exten</c>; see <see cref="Exten"/>.</summary>
    public string? Extension { get; set; }

    public string? AccountCode { get; set; }
    public string? LinkedId { get; set; }

    /// <summary>The dialplan context of the step, from the <c>Context</c> header.</summary>
    public string? Context { get; set; }

    /// <summary>The extension of the step, from the <c>Exten</c> header, the name every other channel event uses.</summary>
    public string? Exten { get; set; }

    /// <summary>The priority of the step, from the <c>Priority</c> header; <see langword="null"/> when it is not a number.</summary>
    public int? Priority { get; set; }
}
