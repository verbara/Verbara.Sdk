namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

/// <summary>
/// Marks a test that requires the realtime stack (PostgreSQL + Asterisk).
/// The stack is started by the RealtimeDbFixture (Testcontainers) before tests run,
/// so no port-based skip check is needed here.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealtimeFactAttribute : FactAttribute;
