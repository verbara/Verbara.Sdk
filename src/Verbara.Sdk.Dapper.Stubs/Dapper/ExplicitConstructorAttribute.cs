namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.ExplicitConstructorAttribute</c>. Working — pure Attribute subclass,
/// no body needed. Consumers may decorate their row constructors with [ExplicitConstructor] to hint
/// the Dapper.AOT generator (the analyzer may or may not honor this; real-Dapper consumers tolerate it).
/// </summary>
[AttributeUsage(AttributeTargets.Constructor)]
public sealed class ExplicitConstructorAttribute : Attribute;
