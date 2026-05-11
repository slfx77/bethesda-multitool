namespace FalloutXbox360Utils.Core.Formats.Esm.Reporting;

/// <summary>
///     Sink for events emitted by the DMP→ESP conversion pipeline.
///     Implementations must be safe to call from any thread.
/// </summary>
public interface IConversionProgressSink
{
    void OnPhaseStart(string phase, int? totalItems);
    void OnEvent(ConversionProgressEvent evt);
    void OnPhaseEnd(string phase, ConversionPipelineStats partialStats);
    void OnComplete(ConversionPipelineStats stats);
}

/// <summary>
///     A sink that ignores everything. Useful for tests and CLI runs without progress UI.
/// </summary>
public sealed class NullConversionProgressSink : IConversionProgressSink
{
    public static readonly NullConversionProgressSink Instance = new();

    public void OnPhaseStart(string phase, int? totalItems)
    {
    }

    public void OnEvent(ConversionProgressEvent evt)
    {
    }

    public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
    {
    }

    public void OnComplete(ConversionPipelineStats stats)
    {
    }
}

/// <summary>
///     Helper extension methods for emitting events without manually constructing
///     <see cref="ConversionProgressEvent" /> records.
/// </summary>
public static class ConversionProgressSinkExtensions
{
    public static void Info(this IConversionProgressSink sink, string phase, string message,
        string? formType = null, uint? formId = null, string? code = null)
    {
        sink.OnEvent(new ConversionProgressEvent
        {
            Timestamp = DateTimeOffset.Now,
            Severity = ConversionEventSeverity.Info,
            Phase = phase,
            FormType = formType,
            FormId = formId,
            Message = message,
            Code = code
        });
    }

    public static void Decision(this IConversionProgressSink sink, string phase, string message,
        string? formType = null, uint? formId = null, string? code = null)
    {
        sink.OnEvent(new ConversionProgressEvent
        {
            Timestamp = DateTimeOffset.Now,
            Severity = ConversionEventSeverity.Decision,
            Phase = phase,
            FormType = formType,
            FormId = formId,
            Message = message,
            Code = code
        });
    }

    public static void Warn(this IConversionProgressSink sink, string phase, string message,
        string? formType = null, uint? formId = null, string? code = null)
    {
        sink.OnEvent(new ConversionProgressEvent
        {
            Timestamp = DateTimeOffset.Now,
            Severity = ConversionEventSeverity.Warning,
            Phase = phase,
            FormType = formType,
            FormId = formId,
            Message = message,
            Code = code
        });
    }

    public static void Error(this IConversionProgressSink sink, string phase, string message,
        string? formType = null, uint? formId = null, string? code = null)
    {
        sink.OnEvent(new ConversionProgressEvent
        {
            Timestamp = DateTimeOffset.Now,
            Severity = ConversionEventSeverity.Error,
            Phase = phase,
            FormType = formType,
            FormId = formId,
            Message = message,
            Code = code
        });
    }
}
