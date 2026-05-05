using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;

namespace Verbara.Sdk.FunctionalTests.Infrastructure.Collections;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xunit collection definition class must end in 'Collection'")]
[CollectionDefinition("Realtime")]
public sealed class RealtimeCollection : ICollectionFixture<RealtimeDbFixture>;
