namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

public abstract class FunctionalTestBase : IAsyncLifetime, IDisposable
{
    protected LogCapture LogCapture { get; } = new();
    protected MetricsCapture MetricsCapture { get; }
    protected ILoggerFactory LoggerFactory { get; }

    protected FunctionalTestBase(params string[] meterNames)
    {
        MetricsCapture = new MetricsCapture(meterNames);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            b.AddProvider(LogCapture).SetMinimumLevel(LogLevel.Debug));
    }

    public virtual Task InitializeAsync() => Task.CompletedTask;
    public virtual Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        MetricsCapture.Dispose();
        LoggerFactory.Dispose();
        GC.SuppressFinalize(this);
    }
}
