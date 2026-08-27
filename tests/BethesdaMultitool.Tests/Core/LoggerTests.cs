using BethesdaMultitool.Core.Diagnostics;
using Xunit;

namespace BethesdaMultitool.Tests.Core;

[Collection("Logger")]
public class LoggerTests : IDisposable
{
    private readonly StringWriter _output = new();

    public LoggerTests()
    {
        // Reset logger before each test to ensure clean state
        Logger.Instance.Reset();
        Logger.SetOutput(_output);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Logger.Instance.Reset();
            _output.Dispose();
        }
    }

    #region LogLevel Enum Tests

    [Fact]
    public void LogLevel_Values_AreCorrectlyOrdered()
    {
        Assert.Equal(0, (int)LogLevel.None);
        Assert.Equal(1, (int)LogLevel.Error);
        Assert.Equal(2, (int)LogLevel.Warn);
        Assert.Equal(3, (int)LogLevel.Info);
        Assert.Equal(4, (int)LogLevel.Debug);
        Assert.Equal(5, (int)LogLevel.Trace);
    }

    #endregion

    #region Singleton Tests

    [Fact]
    public void Instance_ReturnsSameInstance()
    {
        var instance1 = Logger.Instance;
        var instance2 = Logger.Instance;
        Assert.Same(instance1, instance2);
    }

    #endregion

    #region Level Property Tests

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Warn)]
    [InlineData(LogLevel.Info)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    public void Level_CanBeSetToAnyValue(LogLevel level)
    {
        Logger.Instance.Level = level;
        Assert.Equal(level, Logger.Instance.Level);
    }

    #endregion

    #region SetOutput Tests

    [Fact]
    public void SetOutput_ChangesOutputDestination()
    {
        var customWriter = new StringWriter();
        Logger.SetOutput(customWriter);
        Logger.Instance.Info("custom output test");

        Assert.Contains("custom output test", customWriter.ToString());
        Assert.Empty(_output.ToString());

        customWriter.Dispose();
    }

    #endregion

    #region Default State Tests

    [Fact]
    public void Reset_SetsDefaultLevel_ToInfo()
    {
        Logger.Instance.Level = LogLevel.Trace;
        Logger.Instance.Reset();
        Assert.Equal(LogLevel.Info, Logger.Instance.Level);
    }

    [Fact]
    public void Reset_SetsIncludeTimestamp_ToFalse()
    {
        Logger.Instance.IncludeTimestamp = true;
        Logger.Instance.Reset();
        Assert.False(Logger.Instance.IncludeTimestamp);
    }

    [Fact]
    public void Reset_SetsIncludeLevel_ToTrue()
    {
        Logger.Instance.IncludeLevel = false;
        Logger.Instance.Reset();
        Assert.True(Logger.Instance.IncludeLevel);
    }

    #endregion

    #region SetVerbose Tests

    [Fact]
    public void SetVerbose_True_SetsLevelToDebug()
    {
        Logger.Instance.SetVerbose(true);
        Assert.Equal(LogLevel.Debug, Logger.Instance.Level);
    }

    [Fact]
    public void SetVerbose_False_SetsLevelToInfo()
    {
        Logger.Instance.Level = LogLevel.Trace;
        Logger.Instance.SetVerbose(false);
        Assert.Equal(LogLevel.Info, Logger.Instance.Level);
    }

    #endregion

    #region IsEnabled Tests

    [Theory]
    [InlineData(LogLevel.Info, LogLevel.Error, true)]
    [InlineData(LogLevel.Info, LogLevel.Warn, true)]
    [InlineData(LogLevel.Info, LogLevel.Info, true)]
    [InlineData(LogLevel.Info, LogLevel.Debug, false)]
    [InlineData(LogLevel.Info, LogLevel.Trace, false)]
    [InlineData(LogLevel.Error, LogLevel.Error, true)]
    [InlineData(LogLevel.Error, LogLevel.Warn, false)]
    [InlineData(LogLevel.Trace, LogLevel.Trace, true)]
    [InlineData(LogLevel.Trace, LogLevel.Error, true)]
    public void IsEnabled_RespectsCurrentLevel(LogLevel currentLevel, LogLevel checkLevel, bool expected)
    {
        Logger.Instance.Level = currentLevel;
        Assert.Equal(expected, Logger.Instance.IsEnabled(checkLevel));
    }

    [Fact]
    public void IsEnabled_None_AlwaysReturnsFalse()
    {
        Logger.Instance.Level = LogLevel.Trace;
        Assert.False(Logger.Instance.IsEnabled(LogLevel.None));
    }

    #endregion

    #region Per-level write contracts

    /// <summary>
    ///     Every severity behaves identically apart from its threshold and prefix, so the five
    ///     levels are a table and each contract is asserted once. Previously this was 17 near-
    ///     identical facts (one per level per contract), and suppression was only checked for
    ///     Debug and Trace.
    /// </summary>
    public static TheoryData<LogLevelCase> LogLevels => new()
    {
        new LogLevelCase(LogLevel.Error, "[ERR]", LogLevel.None,
            (logger, message) => logger.Error(message),
            (logger, format, args) => logger.Error(format, args)),
        new LogLevelCase(LogLevel.Warn, "[WRN]", LogLevel.Error,
            (logger, message) => logger.Warn(message),
            (logger, format, args) => logger.Warn(format, args)),
        new LogLevelCase(LogLevel.Info, "[INF]", LogLevel.Warn,
            (logger, message) => logger.Info(message),
            (logger, format, args) => logger.Info(format, args)),
        new LogLevelCase(LogLevel.Debug, "[DBG]", LogLevel.Info,
            (logger, message) => logger.Debug(message),
            (logger, format, args) => logger.Debug(format, args)),
        new LogLevelCase(LogLevel.Trace, "[TRC]", LogLevel.Debug,
            (logger, message) => logger.Trace(message),
            (logger, format, args) => logger.Trace(format, args))
    };

    [Theory]
    [MemberData(nameof(LogLevels))]
    public void Write_AtItsOwnLevel_EmitsTheMessage(LogLevelCase level)
    {
        Logger.Instance.Level = level.Level;

        level.Write(Logger.Instance, "the message");

        Assert.Contains("the message", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LogLevels))]
    public void Write_AtItsOwnLevel_EmitsTheLevelPrefix(LogLevelCase level)
    {
        Logger.Instance.Level = level.Level;

        level.Write(Logger.Instance, "the message");

        Assert.Contains(level.Prefix, _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LogLevels))]
    public void Write_WithFormatArgs_SubstitutesThem(LogLevelCase level)
    {
        Logger.Instance.Level = level.Level;

        level.WriteFormatted(Logger.Instance, "value {0} and {1}", [42, "arg"]);

        Assert.Contains("value 42 and arg", _output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A message must be dropped when the configured threshold sits one step below its own
    ///     severity — this is what makes the level a filter rather than a label.
    /// </summary>
    [Theory]
    [MemberData(nameof(LogLevels))]
    public void Write_WhenThresholdIsOneStepStricter_EmitsNothing(LogLevelCase level)
    {
        Logger.Instance.Level = level.SuppressedWhenThresholdIs;

        level.Write(Logger.Instance, "the message");

        Assert.Empty(_output.ToString());
    }

    /// <summary>One severity and the behaviour it must exhibit. Named for a readable case display.</summary>
    public sealed record LogLevelCase(
        LogLevel Level,
        string Prefix,
        LogLevel SuppressedWhenThresholdIs,
        Action<Logger, string> Write,
        Action<Logger, string, object?[]> WriteFormatted)
    {
        public override string ToString()
        {
            return Level.ToString();
        }
    }

    #endregion

    #region Log Tests

    [Fact]
    public void Log_DoesNotWrite_WhenDisabled()
    {
        Logger.Instance.Level = LogLevel.None;
        Logger.Instance.Log(LogLevel.Error, "test");
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public void Log_WritesMessage_WhenEnabled()
    {
        Logger.Instance.Level = LogLevel.Info;
        Logger.Instance.Log(LogLevel.Info, "test message");
        Assert.Contains("test message", _output.ToString());
    }

    #endregion

    #region IncludeLevel Tests

    [Fact]
    public void IncludeLevel_True_AddsPrefix()
    {
        Logger.Instance.IncludeLevel = true;
        Logger.Instance.Info("test");
        Assert.Contains("[INF]", _output.ToString());
    }

    [Fact]
    public void IncludeLevel_False_OmitsPrefix()
    {
        Logger.Instance.IncludeLevel = false;
        Logger.Instance.Info("test");
        Assert.DoesNotContain("[INF]", _output.ToString());
    }

    #endregion

    #region IncludeTimestamp Tests

    [Fact]
    public void IncludeTimestamp_True_AddsTimestamp()
    {
        Logger.Instance.IncludeTimestamp = true;
        Logger.Instance.Info("test");
        var output = _output.ToString();
        // Timestamp format: [HH:mm:ss.fff]
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", output);
    }

    [Fact]
    public void IncludeTimestamp_False_OmitsTimestamp()
    {
        Logger.Instance.IncludeTimestamp = false;
        Logger.Instance.Info("test");
        var output = _output.ToString();
        Assert.DoesNotMatch(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", output);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void Logger_AllLevels_WriteCorrectPrefixes()
    {
        Logger.Instance.Level = LogLevel.Trace;

        Logger.Instance.Error("e");
        Logger.Instance.Warn("w");
        Logger.Instance.Info("i");
        Logger.Instance.Debug("d");
        Logger.Instance.Trace("t");

        var output = _output.ToString();
        Assert.Contains("[ERR] e", output);
        Assert.Contains("[WRN] w", output);
        Assert.Contains("[INF] i", output);
        Assert.Contains("[DBG] d", output);
        Assert.Contains("[TRC] t", output);
    }

    [Fact]
    public void Log_AtWarnThreshold_KeepsErrorAndWarnAndDropsTheRest()
    {
        Logger.Instance.Level = LogLevel.Warn;

        Logger.Instance.Error("error");
        Logger.Instance.Warn("warn");
        Logger.Instance.Info("info");
        Logger.Instance.Debug("debug");
        Logger.Instance.Trace("trace");

        var output = _output.ToString();
        Assert.Contains("error", output);
        Assert.Contains("warn", output);
        Assert.DoesNotContain("info", output);
        Assert.DoesNotContain("debug", output);
        Assert.DoesNotContain("trace", output);
    }

    [Fact]
    public void Logger_MultipleFormats_InSingleMessage()
    {
        Logger.Instance.Info("Count: {0}, Name: {1}, Value: {2:F2}", 10, "test", 3.14159);
        Assert.Contains("Count: 10, Name: test, Value: 3.14", _output.ToString());
    }

    #endregion
}