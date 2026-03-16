using System.Net.Sockets;

namespace Asterisk.Sdk.IntegrationTests.Infrastructure;

/// <summary>
/// Fact attribute that skips the test when Asterisk AMI is not reachable.
/// Prevents noisy failures in CI when Docker is not available.
/// </summary>
public sealed class AsteriskAvailableFactAttribute : FactAttribute
{
    public AsteriskAvailableFactAttribute()
    {
        if (!IsAsteriskReachable())
            Skip = "Asterisk is not reachable (no Docker or not running)";
    }

    private static bool IsAsteriskReachable()
    {
        var host = Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
        var port = int.TryParse(Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT"), out var p) ? p : 5038;

        try
        {
            using var client = new TcpClient();
            client.Connect(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
