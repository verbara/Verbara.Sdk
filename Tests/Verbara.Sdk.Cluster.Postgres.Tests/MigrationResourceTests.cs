using System.Security.Cryptography;
using FluentAssertions;
using Verbara.Sdk.Cluster.Postgres.Migrations;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

/// <summary>
/// The migrations the package embeds. No database: these run in the unit lane.
/// </summary>
public sealed class MigrationResourceTests
{
    private const string V001 = "Verbara.Sdk.Cluster.Postgres.Migrations.V001__DistributedLockSchema.sql";
    private const string V002 = "Verbara.Sdk.Cluster.Postgres.Migrations.V002__DistributedLockAcquiredAt.sql";

    /// <summary>sha256 of the V001 resource inside the published 2.10.1 package.</summary>
    private const string PublishedV001Sha256 = "a8a1d37bcd489337e452f9744f843a01e075c07b466ae8a76df517d16464eb07";

    [Fact]
    public void Package_ShouldEmbedV001AndV002_WhenBuilt()
    {
        ResourceNames().Should().BeEquivalentTo([V001, V002]);
    }

    [Fact]
    public void V001_ShouldKeepItsPublishedBytes_WhenV002IsAdded()
    {
        // V001 runs on every start against databases that already have the table; its bytes stay as shipped.
        Convert.ToHexStringLower(SHA256.HashData(ResourceBytes(V001))).Should().Be(PublishedV001Sha256);
    }

    [Fact]
    public void V002_ShouldOnlyAddTheColumnIfMissing_WhenRead()
    {
        var sql = System.Text.Encoding.UTF8.GetString(ResourceBytes(V002));

        sql.Should().Contain("ADD COLUMN IF NOT EXISTS acquired_at TIMESTAMPTZ");
        sql.Should().NotContain("DEFAULT", "existing rows stay NULL (unknown), never an invented time");
        sql.Should().NotContain("NOT NULL");
    }

#pragma warning disable RS0030 // Banned API: the test reads the package's embedded resources by constant name
    private static string[] ResourceNames() => typeof(MigrationRunner).Assembly.GetManifestResourceNames();

    private static byte[] ResourceBytes(string name)
    {
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
#pragma warning restore RS0030
}
