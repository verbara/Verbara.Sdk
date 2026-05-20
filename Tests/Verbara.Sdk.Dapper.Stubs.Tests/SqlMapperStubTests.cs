using System.Data;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Spot-check that representative SqlMapper extension methods throw the expected
/// <see cref="NotSupportedException"/>. Comprehensive coverage of all 130+ methods would be
/// redundant — <see cref="AotAnnotationsTests"/> + <see cref="PublicApiSurfaceTests"/> already
/// gate the contract. This exercises the runtime behavior of the canonical pattern: sync
/// methods throw directly; async methods return <c>Task.FromException&lt;T&gt;</c> so awaiters
/// observe the failure without thread-pool starvation on a synchronous throw.
/// </summary>
public sealed class SqlMapperStubTests
{
    private static MockConnection Cnn() => new();

    [Fact]
    public void Execute_ShouldThrowNotSupported()
    {
        Action act = () => Cnn().Execute("SELECT 1");
        act.Should().Throw<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFaultedTask_WhenAwaited()
    {
        Func<Task> act = async () => await Cnn().ExecuteAsync("SELECT 1");
        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }

    [Fact]
    public void Query_ShouldThrowNotSupported()
    {
        Action act = () =>
        {
            var rows = Cnn().Query<int>("SELECT 1").ToList();
            rows.Should().NotBeNull(); // never reached — Query throws synchronously
        };
        act.Should().Throw<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }

    [Fact]
    public async Task QueryAsync_ShouldReturnFaultedTask_WhenAwaited()
    {
        Func<Task> act = async () => _ = await Cnn().QueryAsync<int>("SELECT 1");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_ShouldReturnFaultedTask_WhenAwaited()
    {
        Func<Task> act = async () => _ = await Cnn().QuerySingleOrDefaultAsync<int>("SELECT 1");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    /// <summary>
    /// Minimal <see cref="IDbConnection"/> stand-in. The stub extension methods throw before
    /// ever touching the connection, so all members raise <see cref="PlatformNotSupportedException"/>
    /// — if any of them ever executes, the test has already failed its intent.
    /// </summary>
    private sealed class MockConnection : IDbConnection
    {
        [AllowNull]
        public string ConnectionString { get; set; } = "";
        public int ConnectionTimeout => 0;
        public string Database => "";
        public ConnectionState State => ConnectionState.Open;
        public IDbTransaction BeginTransaction() => throw new PlatformNotSupportedException();
        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new PlatformNotSupportedException();
        public void ChangeDatabase(string databaseName) { }
        public void Close() { }
        public IDbCommand CreateCommand() => throw new PlatformNotSupportedException();
        public void Open() { }
        public void Dispose() { }
    }
}
