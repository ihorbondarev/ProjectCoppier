using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Infrastructure;

/// <summary>Null-safe helpers for reporting progress lines.</summary>
public static class LogExtensions
{
    public static void Info(this IProgress<ProgressReport>? log, string message) => log?.Report(ProgressReport.Info(message));
    public static void Success(this IProgress<ProgressReport>? log, string message) => log?.Report(ProgressReport.Success(message));
    public static void Warning(this IProgress<ProgressReport>? log, string message) => log?.Report(ProgressReport.Warning(message));
    public static void Error(this IProgress<ProgressReport>? log, string message) => log?.Report(ProgressReport.Error(message));
    public static void Step(this IProgress<ProgressReport>? log, string message) => log?.Report(ProgressReport.Step(message));
}
