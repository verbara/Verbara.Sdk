using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

public sealed class OpenAiRealtimeClosingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const string Marker = "{\"type\":\"input_audio_buffer.speech_started\"}";
    private const string Meter = "Verbara.Sdk.VoiceAi.OpenAiRealtime";

    private async Task ObserveVendorAnswerAsync(RealtimeFakeServer fake)
    {
        var observationFault = await Record.ExceptionAsync(() => fake.CloseObservationPending.WaitAsync(Limit));
        if (observationFault is null)
            fake.ClientCloseReceived.IsCompleted.Should().BeFalse("the scheduling gate still holds peer publication after handler completion");
        fake.ReleaseCloseObservation();
        if (observationFault is null) await fake.ClientCloseReceived.WaitAsync(Limit);
        output.WriteLine($"peerCloseReadBeforePublication={fake.CloseObservationPending.IsCompletedSuccessfully}; " +
            $"peerClosePublished={fake.ClientCloseReceived.IsCompletedSuccessfully}; observationFault={observationFault?.GetType().Name}");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(3000)]
    public async Task HandleSessionAsync_ShouldAnswerVendorClose_WhenTheVendorClosesFirst(int code)
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true, HoldCloseObservation = true };
        fake.Start();
        await using var rig = await AudioRig.StartAsync();
        await using var bridge = Create(fake);
        using var metrics = new MeterCapture(Meter);
        var run = bridge.HandleSessionAsync(rig.Session, CancellationToken.None).AsTask();
        await fake.SessionUpdateReceived.WaitAsync(Limit);
        await fake.SendCloseAsync((WebSocketCloseStatus)code, "vendor close");
        var fault = await Record.ExceptionAsync(() => run.WaitAsync(Limit));
        await ObserveVendorAnswerAsync(fake);
        using (new AssertionScope())
        {
            fake.CloseObservationPending.IsCompletedSuccessfully.Should().BeTrue("the peer actually crossed the observation scheduling gate");
            fake.ClientCloseReceived.IsCompletedSuccessfully.Should().BeTrue("the answering close must arrive before the handler releases TCP");
            if (code == 1000) fault.Should().BeNull();
            else (fault as SpeechProviderFailureException)?.Code.Should().Be("3000");
            metrics.Get("openai_realtime.sessions.completed").Should().Be(code == 1000 ? 1 : 0);
            metrics.Get("openai_realtime.sessions.failed").Should().Be(code == 1000 ? 0 : 1);
        }
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(3000)]
    public async Task HandleSessionAsync_ShouldPreserveHeldWriteUntilAnswer_WhenTheVendorClosesFirst(int code)
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true, HoldCloseObservation = true };
        fake.Start();
        await using var rig = await AudioRig.StartAsync();
        await using var bridge = Create(fake);
        using var transport = new HeldWriteTransport();
        bridge.ConnectTransport = transport.ConnectAsync;
        var inputStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedAtInputStop = false;
        var stateAtInputStop = WebSocketState.None;
        CancellationTokenRegistration inputRegistration = default;
        bridge.InputReadStarting = token => inputRegistration = token.Register(() =>
        {
            disposedAtInputStop = transport.Disposed.Task.IsCompleted;
            stateAtInputStop = transport.Socket!.State;
            inputStop.TrySetResult();
            transport.Release.TrySetResult();
        });
        using var metrics = new MeterCapture(Meter);
        using var host = new CancellationTokenSource();
        var run = bridge.HandleSessionAsync(rig.Session, host.Token).AsTask();
        try
        {
            await fake.SessionUpdateReceived.WaitAsync(Limit);
            transport.Arm();
            await rig.Caller.SendAudioAsync(new byte[320]);
            await transport.Entered.Task.WaitAsync(Limit);
            await fake.SendCloseAsync((WebSocketCloseStatus)code, "vendor closes inside audio write");
            // Observe vendor-driven cancellation or the terminal answer; do not require cancellation
            // as future coordination may let the held write finish before stopping its input loop.
            await Task.WhenAny(inputStop.Task, fake.ClientCloseReceived, run).WaitAsync(Limit);
            output.WriteLine($"held socket={transport.Socket?.State}; streamDisposed={transport.Disposed.Task.IsCompleted}; streamWriteTokenCanBeCancelled={transport.WriteTokenCanBeCancelled}; disposedAtInputStop={disposedAtInputStop}; stateAtInputStop={stateAtInputStop}");
            transport.Release.TrySetResult();
            var fault = await Record.ExceptionAsync(() => run.WaitAsync(Limit));
            await ObserveVendorAnswerAsync(fake);
            output.WriteLine($"heldStreamWrite=true; passedTokenCancelled={transport.Cancelled.Task.IsCompleted}; vendorClose={code}; answer={fake.ClientCloseReceived.IsCompletedSuccessfully}; fault={fault?.GetType().Name}");
            using (new AssertionScope())
            {
                transport.Entered.Task.IsCompletedSuccessfully.Should().BeTrue("the input audio entered Stream.WriteAsync, below ManagedWebSocket");
                inputStop.Task.IsCompletedSuccessfully.Should().BeTrue("vendor shutdown must stop caller reads");
                disposedAtInputStop.Should().BeFalse("stopping input reads must preserve the held transport write");
                stateAtInputStop.Should().Be(WebSocketState.CloseReceived, "vendor shutdown must not abort the shared socket before its answer");
                transport.Cancelled.Task.IsCompleted.Should().BeFalse("the held stream write must not be cancelled by vendor shutdown");
                fake.CloseObservationPending.IsCompletedSuccessfully.Should().BeTrue("the peer actually crossed the observation scheduling gate");
                fake.ClientCloseReceived.IsCompletedSuccessfully.Should().BeTrue("vendor-driven input shutdown preserves TCP through the answering close");
                if (fake.ClientCloseReceived.IsCompletedSuccessfully)
                    (await fake.ClientCloseReceived).Should().Be(WebSocketCloseStatus.NormalClosure);
                metrics.Get("openai_realtime.sessions.cancelled").Should().Be(0);
                if (code == 1000) fault.Should().BeNull();
                else fault.Should().BeOfType<SpeechProviderFailureException>().Which.Code.Should().Be("3000");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(code == 1000 ? 1 : 0);
                metrics.Get("openai_realtime.sessions.failed").Should().Be(code == 1000 ? 0 : 1);
            }
        }
        finally
        {
            transport.Release.TrySetResult();
            _ = inputRegistration.Unregister();
            await host.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(3000)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task HandleSessionAsync_ShouldFinishOwnedWriteWithinTheCloseBound_WhenOutputEndsDuringAStalledSend(int code)
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
        fake.Start();
        await using var rig = await AudioRig.StartAsync();
        await using var bridge = Create(fake);
        var clock = new FakeTimeProvider();
        bridge.TimeProvider = clock;
        using var transport = new HeldWriteTransport();
        bridge.ConnectTransport = transport.ConnectAsync;
        var inputStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new CancellationTokenSource();
        CancellationTokenRegistration registration = default;
        bridge.InputReadStarting = token => registration = token.Register(() => inputStop.TrySetResult());
        using var metrics = new MeterCapture(Meter);
        var run = bridge.HandleSessionAsync(rig.Session, host.Token).AsTask();
        try
        {
            await fake.SessionUpdateReceived.WaitAsync(Limit);
            while (clock.TimersArmed.TryRead(out _)) { }
            transport.Arm();
            await rig.Caller.SendAudioAsync(new byte[320]);
            await transport.Entered.Task.WaitAsync(Limit);
            if (code == 0) fake.Abort();
            else if (code == -1) await fake.SendEventAsync("{\"type\":\"response.output_audio.delta\",\"delta\":\"invalid base64!\"}");
            else await fake.SendCloseAsync((WebSocketCloseStatus)code, "vendor ends during permanently held send");
            await inputStop.Task.WaitAsync(Limit);
            if (code == 0)
            {
                // A failed ReceiveAsync already aborts its network stream; this is a green baseline
                // guard, rather than evidence that the terminal coordination bound was armed.
                var receiveFault = await Record.ExceptionAsync(() => run.WaitAsync(Limit));
                receiveFault.Should().BeOfType<WebSocketException>();
                transport.WriteEnded.Task.IsCompletedSuccessfully.Should().BeTrue();
                transport.Disposed.Task.IsCompletedSuccessfully.Should().BeTrue();
                transport.Release.Task.IsCompleted.Should().BeFalse();
                metrics.Get("openai_realtime.sessions.failed").Should().Be(1);
                return;
            }
            // No test action releases this write, including the input cancellation callback.
            var bound = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(Limit);
            using (new AssertionScope())
            {
                bound.Should().Be(TimeSpan.FromSeconds(10), "the existing close bound also bounds terminal write coordination");
                run.IsCompleted.Should().BeFalse("the transport write is still held before the bound");
                transport.Release.Task.IsCompleted.Should().BeFalse();
                if (code > 0)
                {
                    transport.Socket!.State.Should().Be(WebSocketState.CloseReceived);
                    transport.Disposed.Task.IsCompleted.Should().BeFalse("input cancellation preserves the close attempt until its bound");
                }
            }
            clock.Advance(bound);
            var fault = await Record.ExceptionAsync(() => run.WaitAsync(Limit));
            output.WriteLine($"stalledWrite=true; vendorCode={code}; fault={fault?.GetType().Name}; writeEnded={transport.WriteEnded.Task.IsCompleted}; disposed={transport.Disposed.Task.IsCompleted}; released={transport.Release.Task.IsCompleted}");
            using (new AssertionScope())
            {
                (fault is TimeoutException).Should().BeFalse("terminal cleanup cannot wait forever for an input send");
                transport.WriteEnded.Task.IsCompletedSuccessfully.Should().BeTrue("no input transport work outlives the handler");
                transport.Disposed.Task.IsCompletedSuccessfully.Should().BeTrue();
                transport.Release.Task.IsCompleted.Should().BeFalse("only transport teardown ended the stalled write");
                if (code == 1000) fault.Should().BeNull();
                else if (code == 3000) fault.Should().BeOfType<SpeechProviderFailureException>().Which.Code.Should().Be("3000");
                else fault.Should().BeOfType<FormatException>();
                metrics.Get("openai_realtime.sessions.completed").Should().Be(code == 1000 ? 1 : 0);
                metrics.Get("openai_realtime.sessions.failed").Should().Be(code == 1000 ? 0 : 1);
                metrics.Get("openai_realtime.sessions.cancelled").Should().Be(0);
            }
        }
        finally
        {
            transport.Release.TrySetResult();
            _ = registration.Unregister();
            await host.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldObserveTheHeldWriteSeam_WhenItsTransportThrows()
    {
        await using var fake = new RealtimeFakeServer { HoldOpenUntilClientCloses = true };
        fake.Start();
        await using var rig = await AudioRig.StartAsync();
        await using var bridge = Create(fake);
        using var transport = new HeldWriteTransport { ThrowOnHeldWrite = true };
        bridge.ConnectTransport = transport.ConnectAsync;
        using var host = new CancellationTokenSource();
        var run = bridge.HandleSessionAsync(rig.Session, host.Token).AsTask();
        try
        {
            await fake.SessionUpdateReceived.WaitAsync(Limit);
            transport.Arm();
            await rig.Caller.SendAudioAsync(new byte[320]);
            await transport.Entered.Task.WaitAsync(Limit);
            var fault = await Record.ExceptionAsync(() => run.WaitAsync(Limit));
            fault.Should().NotBeNull("throwing in the held-write seam must reach the real session");
            fault.Should().NotBeOfType<TimeoutException>();
            transport.WriteEnded.Task.IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            await host.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(4000, false)]
    [InlineData(1000, true)]
    [InlineData(4000, true)]
    public async Task HandleSessionAsync_ShouldReadVendorCloseAfterTrailingAudio_WhenTheCallerLeft(int code, bool ioFailure)
    {
        await using var fake = new RealtimeFakeServer { AnswerClientCloseOnRequest = true };
        fake.Start();
        await using var rig = await AudioRig.StartAsync();
        await using var bridge = Create(fake);
        using var metrics = new MeterCapture(Meter);
        using var marker = new RealtimeEventCollector(bridge.Events, events => events.OfType<RealtimeSpeechStartedEvent>().Any());
        using var host = new CancellationTokenSource();
        var run = bridge.HandleSessionAsync(rig.Session, host.Token).AsTask();
        try
        {
            await fake.SessionUpdateReceived.WaitAsync(Limit);
            if (ioFailure)
            {
                var field = typeof(AudioSocketSession).GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var original = (PipeWriter)field.GetValue(rig.Session)!;
                field.SetValue(rig.Session, new FailingFlushWriter(original));
            }
            else
            {
                await rig.Caller.SendHangupAsync();
                await fake.ClientCloseReceived.WaitAsync(Limit);
            }
            var audio = Convert.ToBase64String(new byte[960]);
            var sendFault = await Record.ExceptionAsync(async () =>
            {
                await fake.SendEventAsync($"{{\"type\":\"response.output_audio.delta\",\"delta\":\"{audio}\"}}");
                await fake.SendEventAsync($"{{\"type\":\"response.output_audio.delta\",\"delta\":\"{audio}\"}}");
                await fake.SendEventAsync(Marker);
            });
            await Task.WhenAny(marker.Satisfied, run).WaitAsync(Limit);
            var readPastAudio = marker.Satisfied.IsCompletedSuccessfully;
            if (readPastAudio) await fake.ClientCloseReceived.WaitAsync(Limit);
            var answerFault = await Record.ExceptionAsync(() => fake.AnswerClientCloseAsync((WebSocketCloseStatus)code, "after trailing audio"));
            var fault = await Record.ExceptionAsync(() => run.WaitAsync(Limit));
            output.WriteLine($"ioFailure={ioFailure}; vendorCode={code}; readPastAudio={readPastAudio}; sendFault={sendFault}; answerFault={answerFault}; ending={fault}");
            using (new AssertionScope())
            {
                readPastAudio.Should().BeTrue("trailing audio must not end the vendor read loop before its close");
                sendFault.Should().BeNull();
                answerFault.Should().BeNull("the bridge keeps the vendor socket alive for its close");
                if (code == 1000) fault.Should().BeNull();
                else fault.Should().BeOfType<SpeechProviderFailureException>().Which.Code.Should().Be("4000");
                metrics.Get("openai_realtime.sessions.completed").Should().Be(code == 1000 ? 1 : 0);
                metrics.Get("openai_realtime.sessions.failed").Should().Be(code == 1000 ? 0 : 1);
                var rejection = bridge.Rejections.Read();
                rejection.ConsecutiveRejections.Should().Be(code == 1000 ? 0 : 1,
                    "audio the departed caller never received cannot make a vendor rejection served");
                rejection.LastRejection.Should().Be(code == 1000 ? null : 4000);
            }
        }
        finally
        {
            await host.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldCountEveryHookRunCancelled_WhenHostCancellationRacesAnInputSend()
    {
        var failures = new List<string>();
        for (var iteration = 0; iteration < 200; iteration++)
        {
            await using var fake = new RealtimeFakeServer { HoldOpenUntilDisposed = true };
            fake.Start();
            await using var rig = await AudioRig.StartAsync();
            await using var bridge = Create(fake);
            using var metrics = new MeterCapture(Meter);
            using var host = new CancellationTokenSource();
            using var hook = new WebSocketReceiveHook();
            using var ready = new RealtimeEventCollector(bridge.Events, events => events.OfType<RealtimeSpeechStartedEvent>().Any());
            Task? audio = null;
            var run = hook.Watch(() => bridge.HandleSessionAsync(rig.Session, host.Token).AsTask());
            try
            {
                await fake.SessionUpdateReceived.WaitAsync(Limit);
                await fake.SendEventAsync(Marker);
                await ready.Satisfied.WaitAsync(Limit);
                // The caller's send and the vendor partial read run concurrently, as in the original
                // race probe. No send hook or custom invoker changes the interleaving being counted.
                audio = Task.Run(async () =>
                {
                    while (!host.IsCancellationRequested)
                        await rig.Caller.SendAudioAsync(new byte[320]);
                });
                await fake.WaitForClientFrameAsync("input_audio_buffer.append").WaitAsync(Limit);
                hook.Arm(host.Cancel);
                var sendFault = await Record.ExceptionAsync(() => fake.SendEventAsync("{\"type\":\"ignored\",\"padding\":\"" + new string('a', 70000) + "\"}"));
                if (sendFault is not null && !hook.Fired.IsCompletedSuccessfully) throw sendFault;
                await hook.Fired.WaitAsync(Limit);
                await audio;
                var fault = await Record.ExceptionAsync(() => run.WaitAsync(Limit));
                var failed = metrics.Get("openai_realtime.sessions.failed");
                var cancelled = metrics.Get("openai_realtime.sessions.cancelled");
                output.WriteLine($"run={iteration}; hookFired={hook.Fired.IsCompletedSuccessfully}; failed={failed}; cancelled={cancelled}; fault={fault}");
                if (fault is not null || failed != 0 || cancelled != 1) failures.Add($"run={iteration}, failed={failed}, cancelled={cancelled}, fault={fault}");
            }
            finally
            {
                await host.CancelAsync();
                if (audio is not null) await audio.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }
        failures.Should().BeEmpty("all 200 partial-read hook runs must classify host cancellation as cancelled, including send-side socket-state errors");
    }

    private static OpenAiRealtimeBridge Create(RealtimeFakeServer fake) => new(
        Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key", Model = "gpt-realtime", InputFormat = AudioFormat.Slin16Mono8kHz }),
        new RealtimeFunctionRegistry([]), NullLogger<OpenAiRealtimeBridge>.Instance)
        { BaseUri = new Uri($"ws://127.0.0.1:{fake.Port}/") };

    private sealed class AudioRig(AudioSocketSession session, AudioSocketServer server, AudioSocketClient caller) : IAsyncDisposable
    {
        internal AudioSocketSession Session => session;
        internal AudioSocketClient Caller => caller;
        internal static async Task<AudioRig> StartAsync()
        {
            var server = new AudioSocketServer(new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 }, NullLogger<AudioSocketServer>.Instance);
            var accepted = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnSessionStarted += s => { accepted.TrySetResult(s); return ValueTask.CompletedTask; };
            await server.StartAsync(CancellationToken.None);
            var caller = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
            await caller.ConnectAsync();
            return new AudioRig(await accepted.Task.WaitAsync(Limit), server, caller);
        }
        public async ValueTask DisposeAsync()
        {
            await caller.DisposeAsync();
            await server.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
        }
    }
    private sealed class FailingFlushWriter(PipeWriter inner) : PipeWriter
    {
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => ValueTask.FromException<FlushResult>(new IOException("controlled caller transport flush failure"));
    }
}
