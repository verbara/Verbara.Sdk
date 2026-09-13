namespace Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;

using Verbara.Sdk.Ami;
using Verbara.Sdk.Ari;

/// <summary>
/// Cleanup calls whose failure must not fail a test: they undo what a test set up once its
/// assertions have run, or clear what an earlier test left behind. Each one catches only the
/// exceptions its call raises when the connection or the resource is already gone, so any other
/// exception still fails the test.
/// </summary>
public static class BestEffort
{
    /// <summary>
    /// Sends an AMI action and waits for its response. Returns <see langword="false"/> instead of
    /// throwing when the connection is not connected or no response arrives.
    /// </summary>
    /// <remarks>
    /// An AMI error reply, such as removing a queue member that is not in the queue, is a response
    /// and returns <see langword="true"/>. A connection that is not connected throws
    /// <see cref="AmiNotConnectedException"/>. A response that does not arrive in time, a cancelled
    /// token, or a disconnect that cancels the pending action throws
    /// <see cref="OperationCanceledException"/>. A socket error does not reach the sender: the
    /// transport's pumps end the connection instead.
    /// </remarks>
    /// <param name="connection">The connection to send on.</param>
    /// <param name="action">The action to send.</param>
    /// <param name="cancellationToken">Cancels the wait for the response.</param>
    /// <returns><see langword="true"/> when a response arrived; otherwise <see langword="false"/>.</returns>
    public static async Task<bool> SendAsync(
        IAmiConnection connection, ManagerAction action, CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.SendActionAsync(action, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is AmiException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs an ARI REST call. Returns <see langword="false"/> instead of throwing when ARI rejects
    /// the call or cannot be reached.
    /// </summary>
    /// <remarks>
    /// A resource that is already gone throws <see cref="AriNotFoundException"/> (HTTP 404), and any
    /// other non-success status an <see cref="AriException"/>. A connection failure throws
    /// <see cref="HttpRequestException"/>, and a timeout <see cref="OperationCanceledException"/>.
    /// </remarks>
    /// <param name="call">The ARI call to run.</param>
    /// <returns><see langword="true"/> when the call succeeded; otherwise <see langword="false"/>.</returns>
    public static async Task<bool> AriAsync(Func<ValueTask> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (Exception ex) when (ex is AriException or HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}
