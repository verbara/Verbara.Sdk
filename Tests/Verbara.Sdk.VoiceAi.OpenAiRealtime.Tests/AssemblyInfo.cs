using Xunit;

// RealtimeMetrics' instruments are process-wide statics with no tags, so a MeterListener cannot
// attribute a measurement to the test that produced it. The setup-cancellation tests assert on
// those counters, which is only sound while nothing else in this assembly is running. ADR-0045
// made this suite's wall clock a tracked number — see the change's §5.5 for the figure after
// serialisation.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
