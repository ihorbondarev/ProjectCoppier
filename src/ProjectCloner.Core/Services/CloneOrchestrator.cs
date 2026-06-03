using System.Text;
using ProjectCloner.Core.Config;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

/// <summary>
/// Drives the full clone pipeline:
/// 1) verify source is a clean git repo, 2) checkout master + pull, 3) copy with namespace replace,
/// 4) force clean master in the copy, 5) remove bitbucket-pipelines.yml, 6) fresh git init,
/// 7) build gate (React + .NET), 8) create Bitbucket repo + push.
/// </summary>
public sealed class CloneOrchestrator
{
    private readonly IGitService _git;
    private readonly ProjectCopier _copier;
    private readonly PipelineCleaner _cleaner;
    private readonly IBuildRunner _buildRunner;
    private readonly IBitbucketClient _bitbucket;
    private readonly IDatabaseBackupService _backup;

    public CloneOrchestrator(
        IGitService git,
        ProjectCopier copier,
        PipelineCleaner cleaner,
        IBuildRunner buildRunner,
        IBitbucketClient bitbucket,
        IDatabaseBackupService backup)
    {
        _git = git;
        _copier = copier;
        _cleaner = cleaner;
        _buildRunner = buildRunner;
        _bitbucket = bitbucket;
        _backup = backup;
    }

    public async Task<CloneResult> RunAsync(CloneRequest request, AppSettings settings,
        IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        var result = new CloneResult { TargetPath = request.TargetPath };
        try
        {
            var targetNamespace = request.TargetNamespace
                ?? new DirectoryInfo(request.TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

            // --- validations ---
            if (!Directory.Exists(request.SourcePath))
                return Fail(result, $"Source path does not exist: {request.SourcePath}", log);
            if (Directory.Exists(request.TargetPath) && Directory.EnumerateFileSystemEntries(request.TargetPath).Any())
                return Fail(result, $"Target already exists and is not empty: {request.TargetPath}", log);
            if (!await _git.IsRepositoryAsync(request.SourcePath, ct))
                return Fail(result, "Source is not a git repository.", log);

            // --- 1. clean working tree (protect uncommitted work) ---
            // This guarantees the source tree equals committed master, so the copy is already clean —
            // no reset/clean on the copy is needed (and would only fight the namespace replacement).
            log.Step("1/7 Checking source working tree…");
            if (!await _git.IsCleanAsync(request.SourcePath, ct))
                return Fail(result,
                    "Source has uncommitted changes. Commit or stash them first — aborted to protect your work.", log);

            // --- 2. update source ---
            log.Step("2/7 Updating source (checkout master + pull)…");
            var checkout = await _git.CheckoutAsync(request.SourcePath, "master", log, ct);
            if (!checkout.Success) return Fail(result, $"git checkout master failed: {checkout.Combined}", log);
            var pull = await _git.PullAsync(request.SourcePath, log, ct);
            if (!pull.Success) return Fail(result, $"git pull failed: {pull.Combined}", log);

            // --- 3. copy with namespace replacement (excludes .git, node_modules, bin, obj) ---
            log.Step("3/7 Copying project (replacing namespace)…");
            _copier.Copy(request.SourcePath, request.TargetPath, request.SourceNamespace, targetNamespace, log, ct);

            // --- optional MySQL backup (non-fatal), before the pipeline file is removed ---
            if (request.BackupDatabase)
            {
                log.Step("Database backup (optional)…");
                var backedUp = await _backup.TryBackupAsync(request.SourcePath, settings.Database, request.DatabaseName, log, ct);
                if (!backedUp)
                    result.Warnings.Add("Database backup was skipped (see log for details).");
            }

            // --- 4. remove bitbucket-pipelines.yml ---
            log.Step("4/7 Removing bitbucket-pipelines.yml…");
            _cleaner.RemovePipelineFiles(request.TargetPath, log);

            // --- 5. fresh git history ---
            log.Step("5/7 Initializing fresh git history…");
            await _git.InitFreshAsync(request.TargetPath, request.CommitMessage, log, ct);

            // --- 6. build gate ---
            if (request.RunBuilds)
            {
                log.Step("6/7 Building clone (React + .NET)…");
                var build = await _buildRunner.RunAsync(request.TargetPath, log, ct);
                if (!build.Success)
                    return Fail(result, $"Build failed at: {build.FailedStep}. Aborted before pushing.", log);
            }
            else
            {
                log.Warning("Build gate skipped by request.");
            }

            // --- 8. Bitbucket + push ---
            if (request.DryRun)
            {
                result.Success = true;
                log.Success("Dry run complete — Bitbucket repo creation and push were skipped.");
                return result;
            }

            log.Step("7/7 Creating Bitbucket repo and pushing…");
            var slug = request.BitbucketRepoSlug ?? Slugify(targetNamespace);
            var repo = await _bitbucket.CreateRepositoryAsync(settings.Bitbucket, slug, log, ct);
            result.RepositoryUrl = repo.HtmlUrl;

            await _git.AddRemoteAsync(request.TargetPath, "origin", repo.CloneUrl, log, ct);

            var pushUrl = BuildAuthenticatedUrl(repo.CloneUrl, settings.Bitbucket);
            var push = await _git.PushAsync(request.TargetPath, pushUrl, "master", log, ct);
            if (!push.Success) return Fail(result, $"git push failed: {push.Combined}", log);

            result.Success = true;
            log.Success($"Done. Repository: {repo.HtmlUrl}");
            return result;
        }
        catch (OperationCanceledException)
        {
            return Fail(result, "Operation cancelled.", log);
        }
        catch (Exception ex)
        {
            return Fail(result, ex.Message, log);
        }
    }

    private static CloneResult Fail(CloneResult result, string reason, IProgress<ProgressReport>? log)
    {
        result.Success = false;
        result.FailureReason = reason;
        log.Error(reason);
        return result;
    }

    private static string Slugify(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '_' or '.' or '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "repository" : slug;
    }

    /// <summary>Builds a one-shot authenticated push URL so credentials are never stored in .git/config.</summary>
    private static string BuildAuthenticatedUrl(string cloneUrl, BitbucketSettings settings)
    {
        var uri = new Uri(cloneUrl);
        var user = Uri.EscapeDataString(settings.Username);
        var pass = Uri.EscapeDataString(settings.AppPassword);
        return $"{uri.Scheme}://{user}:{pass}@{uri.Host}{uri.PathAndQuery}";
    }
}
