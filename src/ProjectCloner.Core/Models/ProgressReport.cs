namespace ProjectCloner.Core.Models;

/// <summary>Severity of a streamed progress line.</summary>
public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Step
}

/// <summary>A single line of pipeline progress, streamed to the UI via <see cref="IProgress{T}"/>.</summary>
public sealed record ProgressReport(LogLevel Level, string Message)
{
    public static ProgressReport Info(string message) => new(LogLevel.Info, message);
    public static ProgressReport Success(string message) => new(LogLevel.Success, message);
    public static ProgressReport Warning(string message) => new(LogLevel.Warning, message);
    public static ProgressReport Error(string message) => new(LogLevel.Error, message);
    public static ProgressReport Step(string message) => new(LogLevel.Step, message);
}
