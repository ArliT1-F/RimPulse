namespace RimPulse.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public static class LogLevelExtensions
{
    public static string ToShortLabel(this LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };
}
