using System.Text;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

/// <summary>
/// Repairs the clone's root .gitignore, which is inherited verbatim from the source project.
///
/// The two project templates in use are broken in opposite ways, and a clone gets whichever one its
/// source carried:
///  * the legacy template lists build output per project folder by name (/BusinessLogic/bin, …), so
///    anything not on that list — the web project, a renamed or newly added one — leaks bin/obj into
///    the repository;
///  * the newer Atlassian-style template covers build output but blanket-ignores media (*.mp4,
///    *.mov, …), so UI videos never enter the commit. The local build gate still passes because the
///    files are on disk; the failure only surfaces when CI clones the pushed repo and the bundler
///    cannot resolve them.
///
/// The inherited content is left untouched — everything added goes into an appended, marked section,
/// so the clone's .gitignore stays recognizably the source's file.
///
/// Must run after <c>git init</c> but before <c>git add</c>: it asks git itself which files the
/// inherited rules hide, and once they are staged that information is gone.
/// </summary>
public sealed class GitIgnoreNormalizer
{
    public const string FileName = ".gitignore";

    private const string SectionHeader = "# --- added by ProjectCloner ---";

    /// <summary>Build output that must never reach the repository, whatever the source template covered.</summary>
    private static readonly string[] RequiredRules =
    [
        "[Bb]in/", "[Oo]bj/", "node_modules/", "dist/", ".vs/", ".DS_Store"
    ];

    /// <summary>
    /// Assets the UI bundler resolves at build time. A blanket rule on these extensions is what breaks
    /// the pushed repository, so any such file that the sources actually reference gets un-ignored.
    /// </summary>
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".flv", ".wmv", ".webm", ".m4v", ".ogv", ".mkv", ".tiff"
    };

    /// <summary>File types searched for references to an asset.</summary>
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".vue", ".svelte",
        ".css", ".scss", ".sass", ".less", ".html", ".htm", ".cshtml", ".razor", ".json"
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".idea"
    };

    private readonly ProcessRunner _runner;

    public GitIgnoreNormalizer(ProcessRunner runner) => _runner = runner;

    public async Task NormalizeAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        var path = Path.Combine(repoPath, FileName);
        var lines = File.Exists(path)
            ? (await File.ReadAllLinesAsync(path, ct)).ToList()
            : [];

        var missingRules = MissingBuildRules(lines);
        var hiddenAssets = await UnignorableAssetsAsync(repoPath, log, ct);

        if (missingRules.Count == 0 && hiddenAssets.Count == 0)
        {
            log.Info($"{FileName} already covers build output and hides no referenced assets.");
            return;
        }

        var sb = new StringBuilder();
        foreach (var line in lines) sb.AppendLine(line);
        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) sb.AppendLine();
        sb.AppendLine(SectionHeader);

        if (missingRules.Count > 0)
        {
            sb.AppendLine("# Build output the inherited rules did not cover.");
            foreach (var rule in missingRules)
            {
                sb.AppendLine(rule);
                log.Info($"{FileName}: added {rule}");
            }
            if (hiddenAssets.Count > 0) sb.AppendLine();
        }

        if (hiddenAssets.Count > 0)
        {
            sb.AppendLine("# Assets referenced from the sources. A blanket media rule kept these out of the");
            sb.AppendLine("# commit, which builds fine locally and fails on a fresh clone.");
            foreach (var asset in hiddenAssets)
            {
                sb.AppendLine($"!/{asset}");
                log.Success($"{FileName}: un-ignored {asset}");
            }
        }

        try
        {
            await File.WriteAllTextAsync(path, sb.ToString(), ct);
        }
        catch (Exception ex)
        {
            log.Warning($"Could not update {FileName}: {ex.Message}");
        }
    }

    /// <summary>Required rules that no existing line already covers repository-wide.</summary>
    private static IReadOnlyList<string> MissingBuildRules(IEnumerable<string> lines)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
            if (RuleToken(line) is { } token)
                covered.Add(token);

        return RequiredRules.Where(rule => !covered.Contains(RuleToken(rule)!)).ToList();
    }

    /// <summary>
    /// Reduces a rule to the single name it ignores everywhere, or null when it does not ignore one.
    /// "[Bb]in/", "bin/" and "/bin" all yield "bin"; "/BusinessLogic/bin" yields null because it is
    /// scoped to one folder and leaves every other project's bin/ exposed — precisely how the legacy
    /// template leaks build output.
    /// </summary>
    private static string? RuleToken(string line)
    {
        var rule = line.Trim();
        if (rule.Length == 0 || rule[0] is '#' or '!') return null;

        rule = rule.Trim('/');
        if (rule.Length == 0 || rule.Contains('/') || rule.Contains('*') || rule.Contains('?')) return null;

        // Collapse character classes so [Bb]in and [Oo]bj compare equal to bin and obj.
        var sb = new StringBuilder(rule.Length);
        for (var i = 0; i < rule.Length; i++)
        {
            if (rule[i] != '[') { sb.Append(rule[i]); continue; }

            var close = rule.IndexOf(']', i);
            if (close < 0 || close == i + 1) return null; // malformed — leave it alone
            sb.Append(rule[i + 1]);
            i = close;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Assets that the inherited rules hide but the sources reference, so they must be committed.
    /// Unreferenced ones stay ignored — these projects carry unused videos of tens of megabytes.
    /// </summary>
    private async Task<IReadOnlyList<string>> UnignorableAssetsAsync(string repoPath, IProgress<ProgressReport>? log, CancellationToken ct)
    {
        // --directory collapses a fully ignored folder to one entry, so build output does not have to
        // be enumerated file by file — and an asset buried inside such a folder is correctly left alone.
        var listed = await _runner.RunAsync("git",
            ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory"],
            repoPath, cancellationToken: ct);

        if (!listed.Success)
        {
            log.Warning($"Could not check which files {FileName} hides: {listed.Combined}");
            return [];
        }

        var candidates = listed.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.EndsWith('/'))
            .Where(p => AssetExtensions.Contains(Path.GetExtension(p)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0) return [];

        var referenced = FindReferenced(repoPath, candidates, ct);

        foreach (var skipped in candidates.Except(referenced, StringComparer.Ordinal))
            log.Info($"{FileName}: {skipped} stays ignored — nothing references it ({Megabytes(repoPath, skipped)}).");

        return referenced;
    }

    private static IReadOnlyList<string> FindReferenced(string repoPath, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        // Bundler references are written as relative paths, so matching the file name is enough and
        // stays robust against ./img/x.mp4, require('…'), url(…) and import forms alike.
        var byName = candidates
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateSourceFiles(repoPath))
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count == candidates.Count) break;

            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            foreach (var (name, paths) in byName)
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                    foreach (var path in paths)
                        found.Add(path);
        }

        return candidates.Where(found.Contains).ToList();
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] subdirectories;
            string[] files;
            try
            {
                subdirectories = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch { continue; }

            foreach (var directory in subdirectories)
                if (!SkippedDirectories.Contains(Path.GetFileName(directory)))
                    pending.Push(directory);

            foreach (var file in files)
                if (SourceExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
        }
    }

    private static string Megabytes(string repoPath, string relativePath)
    {
        try
        {
            var bytes = new FileInfo(Path.Combine(repoPath, relativePath)).Length;
            return $"{bytes / 1024d / 1024d:0.#} MB";
        }
        catch
        {
            return "size unknown";
        }
    }
}
