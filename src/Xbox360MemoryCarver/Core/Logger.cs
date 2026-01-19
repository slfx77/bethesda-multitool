using System.Globalization;

namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Verbosity levels for logging.
/// </summary>
public enum LogLevel
{
    /// <summary>No output.</summary>
    None = 0,

    /// <summary>Errors only.</summary>
    Error = 1,

    /// <summary>Warnings and above.</summary>
    Warn = 2,

    /// <summary>Informational messages and above.</summary>
    Info = 3,

    /// <summary>Debug/verbose output and above.</summary>
    Debug = 4,

    /// <summary>Trace-level output (most verbose).</summary>
    Trace = 5
}

/// <summary>
///     Simple logger with verbosity levels. Thread-safe singleton pattern.
/// </summary>
public sealed class Logger
{
    private static Logger? _instance;
    private static readonly Lock SyncLock = new();

    private TextWriter _output = Console.Out;

    private Logger()
    {
    }

    /// <summary>
    ///     Gets the singleton logger instance.
    /// </summary>
    public static Logger Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (SyncLock)
                {
                    _instance ??= new Logger();
                }
            }

            return _instance;
        }
    }

    /// <summary>
    ///     Current log level. Messages at or below this level are output.
    /// </summary>
    public LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>
    ///     Whether to include timestamps in log output.
    /// </summary>
    public bool IncludeTimestamp { get; set; }

    /// <summary>
    ///     Whether to include the log level prefix in output.
    /// </summary>
    public bool IncludeLevel { get; set; } = true;

    /// <summary>
    ///     Sets the output writer (default: Console.Out).
    /// </summary>
    public void SetOutput(TextWriter output)
    {
        _output = output;
    }

    /// <summary>
    ///     Configure logger from verbose flag (maps to Debug level).
    /// </summary>
    public void SetVerbose(bool verbose)
    {
        Level = verbose ? LogLevel.Debug : LogLevel.Info;
    }

    /// <summary>
    ///     Check if a given level would be logged.
    /// </summary>
    public bool IsEnabled(LogLevel level)
    {
        return level <= Level && level != LogLevel.None;
    }

    /// <summary>
    ///     Log an error message.
    /// </summary>
    public void Error(string message)
    {
        Log(LogLevel.Error, message);
    }

    /// <summary>
    ///     Log an error message with format args.
    /// </summary>
    public void Error(string format, params object[] args)
    {
        Log(LogLevel.Error, string.Format(CultureInfo.InvariantCulture, format, args));
    }

    /// <summary>
    ///     Log a warning message.
    /// </summary>
    public void Warn(string message)
    {
        Log(LogLevel.Warn, message);
    }

    /// <summary>
    ///     Log a warning message with format args.
    /// </summary>
    public void Warn(string format, params object[] args)
    {
        Log(LogLevel.Warn, string.Format(CultureInfo.InvariantCulture, format, args));
    }

    /// <summary>
    ///     Log an informational message.
    /// </summary>
    public void Info(string message)
    {
        Log(LogLevel.Info, message);
    }

    /// <summary>
    ///     Log an informational message with format args.
    /// </summary>
    public void Info(string format, params object[] args)
    {
        Log(LogLevel.Info, string.Format(CultureInfo.InvariantCulture, format, args));
    }

    /// <summary>
    ///     Log a debug/verbose message.
    /// </summary>
    public void Debug(string message)
    {
        Log(LogLevel.Debug, message);
    }

    /// <summary>
    ///     Log a debug/verbose message with format args.
    /// </summary>
    public void Debug(string format, params object[] args)
    {
        Log(LogLevel.Debug, string.Format(CultureInfo.InvariantCulture, format, args));
    }

    /// <summary>
    ///     Log a trace message (most verbose).
    /// </summary>
    public void Trace(string message)
    {
        Log(LogLevel.Trace, message);
    }

    /// <summary>
    ///     Log a trace message with format args.
    /// </summary>
    public void Trace(string format, params object[] args)
    {
        Log(LogLevel.Trace, string.Format(CultureInfo.InvariantCulture, format, args));
    }

    /// <summary>
    ///     Log a message at the specified level.
    /// </summary>
    public void Log(LogLevel level, string message)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var prefix = BuildPrefix(level);
        _output.WriteLine(prefix + message);
    }

    private string BuildPrefix(LogLevel level)
    {
        var parts = new List<string>(2);

        if (IncludeTimestamp)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "[{0:HH:mm:ss.fff}]", DateTime.Now));
        }

        if (IncludeLevel)
        {
            parts.Add(level switch
            {
                LogLevel.Error => "[ERR]",
                LogLevel.Warn => "[WRN]",
                LogLevel.Info => "[INF]",
                LogLevel.Debug => "[DBG]",
                LogLevel.Trace => "[TRC]",
                _ => ""
            });
        }

        return parts.Count > 0 ? string.Join(" ", parts) + " " : "";
    }

    /// <summary>
    ///     Reset logger to defaults (for testing).
    /// </summary>
    public void Reset()
    {
        Level = LogLevel.Info;
        _output = Console.Out;
        IncludeTimestamp = false;
        IncludeLevel = true;
    }
}
