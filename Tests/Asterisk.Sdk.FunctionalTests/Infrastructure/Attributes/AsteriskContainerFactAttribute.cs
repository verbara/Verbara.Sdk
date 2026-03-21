namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

public sealed class AsteriskContainerFactAttribute : FactAttribute
{
    public AsteriskContainerFactAttribute()
    {
        if (!IsAsteriskReachable())
            Skip = "Asterisk container is not reachable";
    }

    private static bool IsAsteriskReachable()
    {
        var host = Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
        var port = int.Parse(Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT") ?? "5038",
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            return success && client.Connected;
        }
        catch { return false; }
    }
}
