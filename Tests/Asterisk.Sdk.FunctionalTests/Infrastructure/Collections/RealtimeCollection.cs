using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Collections;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xunit collection definition class must end in 'Collection'")]
[CollectionDefinition("Realtime")]
public sealed class RealtimeCollection : ICollectionFixture<RealtimeDbFixture>;
