using System.Data;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Behavioral coverage for the working <c>Dapper.CommandDefinition</c> struct impl:
/// constructor + property getters round-trip + the <c>Buffered</c> flag derived from the
/// <see cref="global::Dapper.CommandFlags"/> bitmask. Real Dapper.AOT-generated interceptor
/// code reads these property getters at runtime to construct the underlying ADO.NET command.
/// </summary>
public sealed class CommandDefinitionTests
{
    [Fact]
    public void Constructor_ShouldStoreAllArguments_WhenAllProvided()
    {
        using var cts = new CancellationTokenSource();
        var def = new global::Dapper.CommandDefinition(
            commandText: "SELECT 1",
            parameters: new { id = 42 },
            transaction: null,
            commandTimeout: 30,
            commandType: CommandType.Text,
            flags: global::Dapper.CommandFlags.Buffered,
            cancellationToken: cts.Token);

        def.CommandText.Should().Be("SELECT 1");
        def.Parameters.Should().NotBeNull();
        def.CommandTimeout.Should().Be(30);
        def.CommandType.Should().Be(CommandType.Text);
        def.Flags.Should().Be(global::Dapper.CommandFlags.Buffered);
        def.CancellationToken.Should().Be(cts.Token);
        def.Buffered.Should().BeTrue();
    }

    [Fact]
    public void Buffered_ShouldBeFalse_WhenFlagsDoNotIncludeBuffered()
    {
        var def = new global::Dapper.CommandDefinition("SELECT 1", flags: global::Dapper.CommandFlags.None);
        def.Buffered.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ShouldDefault_WhenMinimalArgs()
    {
        var def = new global::Dapper.CommandDefinition("SELECT 1");
        def.CommandText.Should().Be("SELECT 1");
        def.Parameters.Should().BeNull();
        def.Transaction.Should().BeNull();
        def.CommandTimeout.Should().BeNull();
        def.CommandType.Should().BeNull();
        def.Flags.Should().Be(global::Dapper.CommandFlags.Buffered);
        def.Buffered.Should().BeTrue();
    }
}
