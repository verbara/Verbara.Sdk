using System.Runtime.CompilerServices;
using Verbara.Sdk.Ami.Connection;

// EventActionOutcome was declared in this assembly up to 2.6.x and now lives in Verbara.Sdk, under the same full name,
// so that IAmiConnection can take it. A package built against it here (a Live package of the 2.x line) still binds.
[assembly: TypeForwardedTo(typeof(EventActionOutcome))]
