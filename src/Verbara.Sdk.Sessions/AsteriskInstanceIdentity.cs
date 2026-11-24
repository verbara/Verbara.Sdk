namespace Verbara.Sdk.Sessions;

/// <summary>
/// The Asterisk instance a session notification came from, as that instance names itself: its <see cref="SystemName"/>,
/// the <c>systemname</c> set in its <c>asterisk.conf</c>, read once per AMI session at the login. Carried by
/// <see cref="SessionDomainEvent.Instance"/> beside <see cref="SessionDomainEvent.ServerId"/>, never instead of it: the
/// server id stays the key the host gave the server, so one Asterisk attached under two server ids carries one identity,
/// and two Asterisk that share a <c>linkedid</c> but not a name carry two.
/// </summary>
/// <remarks>
/// <para>
/// Compared by value, ordinally, as Asterisk wrote the name. An instance that gives no name has no identity: the
/// notification's <see cref="SessionDomainEvent.Instance"/> is <see langword="null"/>, which means unknown, so an identity
/// always names its instance.
/// </para>
/// <para>
/// Not a positional record: a later version may add an optional member without changing this constructor.
/// </para>
/// </remarks>
public sealed record AsteriskInstanceIdentity
{
    /// <summary>The identity of the instance named <paramref name="systemName"/>.</summary>
    /// <param name="systemName">The instance's <c>systemname</c>, exactly as Asterisk reports it.</param>
    /// <exception cref="ArgumentException"><paramref name="systemName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="systemName"/> is <see langword="null"/>.</exception>
    public AsteriskInstanceIdentity(string systemName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        SystemName = systemName;
    }

    /// <summary>
    /// The instance's <c>systemname</c>, as the <c>SystemName</c> of its AMI <c>CoreSettings</c> response reports it;
    /// never empty.
    /// </summary>
    public string SystemName { get; }
}
