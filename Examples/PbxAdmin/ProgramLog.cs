using Microsoft.Extensions.Logging;

namespace PbxAdmin;

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "[REALTIME] Server {ServerId}: missing tables: {Tables}")]
    public static partial void RealtimeMissingTables(ILogger logger, string serverId, string tables);
}
