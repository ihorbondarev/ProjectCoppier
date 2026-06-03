using System.Text.RegularExpressions;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

/// <summary>
/// Recursively copies a project tree, replacing the source namespace with the target namespace
/// in file names and text content, and regenerating the GUID in AssemblyInfo files.
/// Refactor of the original ProjectCoppierCore logic: no singleton, no Console coupling, thread-safe.
/// </summary>
public sealed class ProjectCopier
{
    // Binary file types whose content must not be touched.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".ico", ".svg", ".ttf", ".eot", ".woff", ".woff2",
        ".gif", ".bmp", ".webp", ".xlsx", ".xls", ".dll", ".exe", ".pdb", ".mp4",
        ".mp3", ".pdf", ".zip", ".gz", ".7z", ".db"
    };

    // Directories that are never copied (kept out of the clone entirely).
    // .git is excluded because the clone gets a fresh history (git init) after copying;
    // copying it would also fight the namespace replacement applied to tracked files.
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".svn", ".vs"
    };

    private static readonly Regex GuidPattern = new(
        "[A-F0-9]{8}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Copy(string sourcePath, string targetPath, string sourceNamespace, string targetNamespace,
        IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        var source = new DirectoryInfo(sourcePath);
        if (!source.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourcePath}");

        CopyDirectory(source, targetPath, sourceNamespace, targetNamespace, log, ct);
    }

    private void CopyDirectory(DirectoryInfo source, string targetPath, string srcNs, string dstNs,
        IProgress<ProgressReport>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetPath);

        var options = new ParallelOptions { CancellationToken = ct };

        Parallel.ForEach(source.GetFiles(), options, file =>
        {
            if (file.Extension.Equals(".suo", StringComparison.OrdinalIgnoreCase)) return;

            var destPath = Path.Combine(targetPath, ReplaceNamespace(file.Name, srcNs, dstNs));
            file.CopyTo(destPath, overwrite: true);

            if (!BinaryExtensions.Contains(file.Extension))
                ReplaceContent(destPath, srcNs, dstNs, log);

            log.Info($"Copied {destPath}");
        });

        Parallel.ForEach(source.GetDirectories(), options, dir =>
        {
            if (ExcludedDirectories.Contains(dir.Name))
            {
                log.Info($"Skipped {dir.Name}/");
                return;
            }

            var subTarget = Path.Combine(targetPath, ReplaceNamespace(dir.Name, srcNs, dstNs));
            CopyDirectory(dir, subTarget, srcNs, dstNs, log, ct);
        });
    }

    private static string ReplaceNamespace(string value, string srcNs, string dstNs)
        => string.IsNullOrEmpty(srcNs) ? value : value.Replace(srcNs, dstNs);

    private void ReplaceContent(string filePath, string srcNs, string dstNs, IProgress<ProgressReport>? log)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            log.Warning($"Skipped content of {Path.GetFileName(filePath)}: {ex.Message}");
            return;
        }

        var updated = string.IsNullOrEmpty(srcNs) ? content : content.Replace(srcNs, dstNs);

        if (filePath.Contains("AssemblyInfo", StringComparison.OrdinalIgnoreCase))
            updated = RegenerateAssemblyGuid(updated, filePath, log);

        if (ReferenceEquals(updated, content)) return; // no change → no rewrite

        try
        {
            File.WriteAllText(filePath, updated);
        }
        catch (Exception ex)
        {
            log.Warning($"Could not write {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    private string RegenerateAssemblyGuid(string content, string filePath, IProgress<ProgressReport>? log)
    {
        var match = GuidPattern.Match(content);
        if (!match.Success) return content;

        var replaced = content.Replace(match.Value, Guid.NewGuid().ToString());
        log.Info($"Assembly GUID regenerated in {Path.GetFileName(filePath)}");
        return replaced;
    }
}
