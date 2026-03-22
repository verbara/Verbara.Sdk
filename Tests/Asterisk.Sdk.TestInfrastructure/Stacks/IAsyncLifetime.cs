namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Matches the xunit IAsyncLifetime contract so stacks can be used directly
/// as xunit collection fixtures without introducing a hard xunit dependency
/// in the TestInfrastructure library.
/// </summary>
public interface IAsyncLifetime
{
    Task InitializeAsync();
    Task DisposeAsync();
}
