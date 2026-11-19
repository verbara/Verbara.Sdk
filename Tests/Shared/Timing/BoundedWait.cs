namespace Verbara.Sdk.Tests.Shared.Timing;

/// <summary>
/// <c>Task.WhenAny(task, Task.Delay(bound))</c> without the timer left running: the delay is created with a
/// token this helper cancels as soon as the first of the two has finished, so a task that wins does not leave a
/// timer behind for the rest of the run (CA2027).
/// </summary>
/// <remarks>
/// <para>
/// The result is the same as the expression it replaces: <paramref name="task"/> itself when it finished first,
/// in any state, and the delay's task otherwise. A caller that compares the result with
/// <paramref name="task"/>, or asserts on which one won, reads exactly what it read before.
/// </para>
/// <para>
/// The caller still writes the <c>Task.Delay</c>, with its bound, inside the lambda, so the wall-clock window
/// stays on its own line where the sync-fence guard sees it and its <c>// fence-allow:</c> marker excuses it.
/// </para>
/// </remarks>
internal static class BoundedWait
{
    public static async Task<Task> WhenAnyAsync(Task task, Func<CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(delay);

        // The winner is a Task, so it is returned after the scope: returned from inside it, it reads as a task
        // left unawaited while its resources are disposed (MA0100).
        Task first;
        using (var timer = new CancellationTokenSource())
        {
            first = await Task.WhenAny(task, delay(timer.Token)).ConfigureAwait(false);
            await timer.CancelAsync().ConfigureAwait(false);
        }

        return first;
    }
}
