using Asterisk.Sdk.TestInfrastructure.Stacks;

namespace Asterisk.Sdk.IntegrationTests.Infrastructure;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xunit collection definition class must end in 'Collection'")]
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
