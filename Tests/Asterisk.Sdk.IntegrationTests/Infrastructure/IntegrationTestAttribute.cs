namespace Asterisk.Sdk.IntegrationTests.Infrastructure;

/// <summary>
/// Shorthand for [Trait("Category", "Integration")].
/// Marks tests requiring a running Asterisk instance.
/// Filter: dotnet test --filter "Category=Integration"
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class IntegrationTestAttribute : Attribute;
