using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

/// <summary>
/// Asterisk's <c>SendFlash</c>: queues a hook-flash on <see cref="Channel"/>. A live channel is answered
/// <c>Success</c> / <c>Flash successfully queued</c>, one that does not exist <c>Error</c> / <c>Channel not found</c>.
/// </summary>
[VerbaraMapping("SendFlash")]
public sealed class SendFlashAction : ManagerAction
{
    public string? Channel { get; set; }

    /// <summary>
    /// Written as the <c>Receive</c> field (<c>true</c> or <c>false</c>) when set, after <see cref="Channel"/>; not
    /// written when <see langword="null"/>. With <c>true</c>, measured on Asterisk 20.20.1, 22.12.0, 23.6.0 and
    /// 24.0.0, the flash is raised on <see cref="Channel"/> itself; without the field it is raised on the other leg
    /// of the call.
    /// </summary>
    public bool? Receive { get; set; }
}
