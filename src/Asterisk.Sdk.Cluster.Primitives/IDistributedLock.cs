namespace Asterisk.Sdk.Cluster.Primitives;

/// <summary>
/// Abstraction for a cluster-wide mutual-exclusion lock with owner-based semantics and
/// automatic expiry. A lock is scoped by a <em>resource</em> name; ownership is identified
/// by an arbitrary <em>owner</em> token supplied by the caller.
/// </summary>
/// <remarks>
/// Implementations must honor these invariants:
/// <list type="bullet">
/// <item><description>A successful <see cref="TryAcquireAsync"/> call grants exclusive access to <em>resource</em> for the supplied owner until either <see cref="ReleaseAsync"/> is called or the expiry elapses.</description></item>
/// <item><description>Re-acquisition by the same owner is allowed and refreshes the expiry.</description></item>
/// <item><description><see cref="ReleaseAsync"/> is a no-op when the lock is not currently owned by the supplied owner (e.g. expired or owned by another party).</description></item>
/// </list>
/// </remarks>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to acquire the lock for <paramref name="resource"/> on behalf of
    /// <paramref name="owner"/>, with the supplied <paramref name="expiry"/>.
    /// </summary>
    /// <returns><c>true</c> if the caller now holds the lock; <c>false</c> if it is already held by another owner.</returns>
    ValueTask<bool> TryAcquireAsync(string resource, string owner, TimeSpan expiry, CancellationToken cancellationToken = default);

    /// <summary>Releases the lock for <paramref name="resource"/> when owned by <paramref name="owner"/>.</summary>
    ValueTask ReleaseAsync(string resource, string owner, CancellationToken cancellationToken = default);
}
