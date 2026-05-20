using System.Data;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Behavioral coverage for the partial-working <c>Dapper.DynamicParameters</c> impl. The
/// parameterless ctor + typed <c>Add</c> / <c>Get</c> / <c>ParameterNames</c> path is runtime
/// -safe (dictionary-backed). The template ctor + explicit-interface
/// <c>IDynamicParameters.AddParameters</c> dispatcher throw because Dapper.AOT must intercept
/// the parent call site to drive parameter binding without runtime reflection.
/// </summary>
public sealed class DynamicParametersTests
{
    [Fact]
    public void Add_ThenGet_ShouldRoundTripValue()
    {
        var p = new global::Dapper.DynamicParameters();
        p.Add("id", 42, DbType.Int32);
        p.Get<int>("id").Should().Be(42);
    }

    [Fact]
    public void ParameterNames_ShouldEnumerateAllAddedNames()
    {
        var p = new global::Dapper.DynamicParameters();
        p.Add("id", 1);
        p.Add("name", "alice");
        p.ParameterNames.Should().BeEquivalentTo(["id", "name"]);
    }

    [Fact]
    public void Get_ShouldThrow_WhenNameNotFound()
    {
        var p = new global::Dapper.DynamicParameters();
        Action act = () => p.Get<int>("missing");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Constructor_WithTemplate_ShouldThrow_PerDAP015()
    {
        Action act = () => _ = new global::Dapper.DynamicParameters(new { id = 1 });
        act.Should().Throw<NotSupportedException>().WithMessage("*DAP015*");
    }

    [Fact]
    public void AddParameters_ExplicitInterface_ShouldThrow()
    {
        global::Dapper.SqlMapper.IDynamicParameters p = new global::Dapper.DynamicParameters();
        Action act = () => p.AddParameters(command: null!, identity: null!);
        act.Should().Throw<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }
}
