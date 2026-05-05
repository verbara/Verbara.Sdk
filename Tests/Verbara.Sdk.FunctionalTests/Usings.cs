global using Xunit;

// Serialize xUnit collections so FunctionalCollection and RealtimeCollection do not start
// their Docker containers in parallel — CI runners cannot sustain 3+ simultaneous
// Postgres + Asterisk containers.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
