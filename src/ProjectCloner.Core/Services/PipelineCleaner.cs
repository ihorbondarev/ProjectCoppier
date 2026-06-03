using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

/// <summary>Removes Bitbucket CI pipeline files from the clone.</summary>
public sealed class PipelineCleaner
{
    public const string PipelineFileName = "bitbucket-pipelines.yml";

    public IReadOnlyList<string> RemovePipelineFiles(string rootPath, IProgress<ProgressReport>? log = null)
    {
        var removed = new List<string>();
        foreach (var file in FindPipelineFiles(rootPath))
        {
            try
            {
                File.Delete(file);
                removed.Add(file);
                log.Info($"Removed {file}");
            }
            catch (Exception ex)
            {
                log.Warning($"Could not remove {file}: {ex.Message}");
            }
        }

        if (removed.Count == 0) log.Info($"No {PipelineFileName} found.");
        return removed;
    }

    public static string? FindPipelineFile(string rootPath)
        => FindPipelineFiles(rootPath).FirstOrDefault();

    private static IEnumerable<string> FindPipelineFiles(string rootPath)
        => Directory.EnumerateFiles(rootPath, PipelineFileName, SearchOption.AllDirectories);
}
