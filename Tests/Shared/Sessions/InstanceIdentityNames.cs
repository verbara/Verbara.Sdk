using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Tests.Shared.Sessions;

/// <summary>
/// The one place the tests name the Asterisk instance identity a session notification carries: its member on
/// <see cref="SessionDomainEvent"/> and its value type. Every test of the identity reads and builds it through here, so
/// renaming the member or the type is one edit of this file.
/// </summary>
/// <remarks>
/// The names are placeholders until the owner approves them before the identity ships: the member <c>Instance</c> and
/// the type <c>AsteriskInstanceIdentity</c>, whose one member today is the instance's <c>SystemName</c>.
/// </remarks>
internal static class InstanceIdentityNames
{
    /// <summary>The member's name, as a record prints it.</summary>
    public const string MemberName = "Instance";

    /// <summary>The member's id in the XML documentation file the package ships.</summary>
    public const string DocumentationId = "P:Verbara.Sdk.Sessions.SessionDomainEvent." + MemberName;

    /// <summary>The identity the notification carries, <see langword="null"/> when it names no instance.</summary>
    public static object? IdentityOf(SessionDomainEvent notification) => notification.Instance;

    /// <summary>The instance's <c>SystemName</c>, <see langword="null"/> when the notification names no instance.</summary>
    public static string? SystemNameOf(SessionDomainEvent notification) => notification.Instance?.SystemName;

    /// <summary>The value type's instance naming <paramref name="systemName"/>.</summary>
    public static object NewIdentity(string systemName) => new AsteriskInstanceIdentity(systemName);

    /// <summary>
    /// A copy of <paramref name="notification"/> naming the instance <paramref name="systemName"/>, or naming none when it
    /// is <see langword="null"/>.
    /// </summary>
    public static SessionDomainEvent WithSystemName(SessionDomainEvent notification, string? systemName) =>
        notification with { Instance = systemName is null ? null : new AsteriskInstanceIdentity(systemName) };
}
