using FluentAssertions;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.Audio;

public sealed class AudioStreamAdmissionTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void TryEnter_ShouldAdmitUpToTheLimitAndRefuseTheNext_WhenNothingIsFreed()
    {
        var admission = new AudioStreamAdmission();

        admission.TryEnter(3).Should().BeTrue();
        admission.TryEnter(3).Should().BeTrue();
        admission.TryEnter(3).Should().BeTrue();
        admission.TryEnter(3).Should().BeFalse("three places are held and the limit is three");
        admission.Held.Should().Be(3, "a refused connection takes no place");
    }

    [Fact]
    public void Exit_ShouldFreeExactlyOnePlace_WhenTheLimitIsReached()
    {
        var admission = new AudioStreamAdmission();
        admission.TryEnter(2).Should().BeTrue();
        admission.TryEnter(2).Should().BeTrue();

        admission.Exit();

        admission.Held.Should().Be(1);
        admission.TryEnter(2).Should().BeTrue("the place the ended connection gave back is free");
        admission.TryEnter(2).Should().BeFalse("only one place was given back");
    }

    [Fact]
    public void TryEnter_ShouldRefuseUntilEnoughPlacesAreFreed_WhenTheLimitIsLoweredBelowHeld()
    {
        var admission = new AudioStreamAdmission();
        for (var i = 0; i < 4; i++)
            admission.TryEnter(4).Should().BeTrue();

        // The limit is a settable option read on every accept, so it can drop below what is held.
        admission.TryEnter(2).Should().BeFalse("four are held and the limit is now two");
        admission.Exit();
        admission.TryEnter(2).Should().BeFalse("three are still held");
        admission.Exit();
        admission.TryEnter(2).Should().BeFalse("two are still held, which is the limit");
        admission.Exit();

        admission.Held.Should().Be(1);
        admission.TryEnter(2).Should().BeTrue("one is held and the limit is two");
        admission.Held.Should().Be(2);
    }

    [Fact]
    public void TryEnter_ShouldAdmitExactlyTheLimit_WhenManyThreadsEnterAtOnce()
    {
        var workers = Environment.ProcessorCount * 4;
        // Fewer places than cores: the workers that run at once race past the last free place.
        var limit = Math.Max(1, Environment.ProcessorCount / 2);

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var admission = new AudioStreamAdmission();
            var admitted = 0;
            var go = 0;
            // A barrier alone wakes its waiters one by one, microseconds apart, which is wider than the
            // window between a separate check and increment: measured, a check-then-increment TryEnter
            // passed 200 barrier-only iterations. So every worker, once past the barrier, polls one flag
            // (yielding, so the workers not yet scheduled reach it), and the flag is raised only when all
            // of them poll it: the workers that are running then leave together.
            using var barrier = new Barrier(workers);
            using var polling = new CountdownEvent(workers);

            // Dedicated threads, not pool tasks: a barrier wider than the pool would stall on pool tasks.
            var threads = new Thread[workers];
            for (var w = 0; w < workers; w++)
            {
                threads[w] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    polling.Signal();
                    while (Volatile.Read(ref go) == 0)
                        Thread.Yield();
                    if (admission.TryEnter(limit))
                        Interlocked.Increment(ref admitted);
                })
                { IsBackground = true };
                threads[w].Start();
            }

            polling.Wait(SignalTimeout).Should().BeTrue("every worker passes the barrier and polls the flag");
            Volatile.Write(ref go, 1);

            foreach (var thread in threads)
                thread.Join(SignalTimeout).Should().BeTrue("every worker returns from one TryEnter call");

            admitted.Should().Be(limit,
                "the check and the increment are one atomic step, so {0} workers released together " +
                "can never pass a limit of {1} (iteration {2})", workers, limit, iteration);
            admission.Held.Should().Be(limit, "iteration {0}", iteration);
        }
    }
}
