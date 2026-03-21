namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

/// <summary>
/// Skips the test if the realtime stack (PostgreSQL + Asterisk) is not reachable.
/// Probes PostgreSQL on port 15432 and AMI on port 15038.
/// </summary>
public sealed class RealtimeFactAttribute : FactAttribute
{
    public RealtimeFactAttribute()
    {
        if (!IsRealtimeReachable())
        {
            Skip = "Realtime stack not reachable (PostgreSQL:15432 + AMI:15038)";
        }
    }

    private static bool IsRealtimeReachable()
    {
        try
        {
            using var pgClient = new System.Net.Sockets.TcpClient();
            pgClient.Connect("localhost", 15432);

            using var amiClient = new System.Net.Sockets.TcpClient();
            amiClient.Connect("localhost", 15038);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
