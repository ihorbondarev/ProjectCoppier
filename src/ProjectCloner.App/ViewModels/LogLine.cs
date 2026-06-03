using ProjectCloner.Core.Models;

namespace ProjectCloner.App.ViewModels;

/// <summary>One coloured line in the log panel.</summary>
public sealed class LogLine
{
    public required string Message { get; init; }
    public required string Color { get; init; }

    public static LogLine From(ProgressReport report) => new()
    {
        Message = report.Message,
        Color = report.Level switch
        {
            LogLevel.Success => "#4CAF50",
            LogLevel.Warning => "#E0A030",
            LogLevel.Error => "#E5534B",
            LogLevel.Step => "#4FA3FF",
            _ => "#D0D0D0"
        }
    };
}
