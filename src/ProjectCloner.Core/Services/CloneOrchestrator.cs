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
///
/// The target may also be a folder holding a freshly cloned, still-empty Bitbucket repository. That
/// repo is then adopted rather than replaced: its .git and origin survive, and step 7 pushes to it
/// instead of creating a second repository.
/// </summary>
public sealed class CloneOrchestrator
{
    /// <summary>
    /// What a newly created Bitbucket repository may legitimately contain when it is cloned. Anything
    /// beyond this means the folder holds real work, and filling it in would overwrite someone's files.
    /// </summary>
    private static readonly HashSet<string> RepositoryScaffold = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".gitignore", ".gitattributes", ".DS_Store", "Thumbs.db",
        "README", "README.md", "README.txt", "README.rst",
        "LICENSE", "LICENSE.md", "LICENSE.txt"
    };

    private readonly IGitService _git;
    private readonly ProjectCopier _copier;
    private readonly PipelineCleaner _cleaner;
    private readonly GitIgnoreNormalizer _gitIgnore;
    private readonly IBuildRunner _buildRunner;
    private readonly IBitbucketClient _bitbucket;
    private readonly IDatabaseBackupService _backup;

    public CloneOrchestrator(
        IGitService git,
        ProjectCopier copier,
        PipelineCleaner cleaner,
        GitIgnoreNormalizer gitIgnore,
        IBuildRunner buildRunner,
        IBitbucketClient bitbucket,
        IDatabaseBackupService backup)
    {
        _git = git;
        _copier = copier;
        _cleaner = cleaner;
        _gitIgnore = gitIgnore;
        _buildRunner = buildRunner;
        _bitbucket = bitbucket;
        _backup = backup;
    }

    public async Task<CloneResult> RunAsync(CloneRequest request, AppSettings settings,
        IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        var result = new CloneResult();
        try
        {
            // Expand ~, env vars and resolve to absolute so terminal-style paths work from the GUI too.
            var sourcePath = PathUtil.Expand(request.SourcePath);
            var targetPath = PathUtil.Expand(request.TargetPath);
            result.TargetPath = targetPath;

            var targetNamespace = request.TargetNamespace
                ?? new DirectoryInfo(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

            // --- validations ---
            if (!Directory.Exists(sourcePath))
                return Fail(result, $"Source path does not exist: {sourcePath}", log);
            if (!await _git.IsRepositoryAsync(sourcePath, ct))
                return Fail(result, "Source is not a git repository.", log);

            // A non-empty target is normally a mistake, but one case is deliberate: the repo was created
            // on Bitbucket and cloned into the future project folder, and the project goes inside it.
            var adoptExistingRepo = false;
            if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
            {
                if (!await _git.IsRepositoryAsync(targetPath, ct))
                    return Fail(result, $"Target already exists and is not empty: {targetPath}", log);

                var occupied = OccupyingEntries(targetPath);
                if (occupied.Count > 0)
                    return Fail(result,
                        $"Target is a git repository that already holds files ({string.Join(", ", occupied.Take(5))}). " +
                        "Use an empty folder, or a folder holding a freshly cloned empty repository.", log);

                adoptExistingRepo = true;
            }

            // --- 1. clean working tree (protect uncommitted work) ---
            // This guarantees the source tree equals committed master, so the copy is already clean —
            // no reset/clean on the copy is needed (and would only fight the namespace replacement).
            log.Step("1/7 Checking source working tree…");
            if (!await _git.IsCleanAsync(sourcePath, ct))
                return Fail(result,
                    "Source has uncommitted changes. Commit or stash them first — aborted to protect your work.", log);

            // --- 2. update source ---
            log.Step("2/7 Updating source (checkout master + pull)…");
            var checkout = await _git.CheckoutAsync(sourcePath, "master", log, ct);
            if (!checkout.Success) return Fail(result, $"git checkout master failed: {checkout.Combined}", log);
            var pull = await _git.PullAsync(sourcePath, BuildGitEnv(settings), log, ct);
            if (!pull.Success) return Fail(result, $"git pull failed: {pull.Combined}", log);

            // --- 3. copy with namespace replacement (excludes .git, node_modules, bin, obj) ---
            log.Step("3/7 Copying project (replacing namespace)…");
            _copier.Copy(sourcePath, targetPath, request.SourceNamespace, targetNamespace, log, ct);

            // --- optional MySQL backup (non-fatal), before the pipeline file is removed ---
            if (request.BackupDatabase)
            {
                log.Step("Database backup (optional)…");
                var backedUp = await _backup.TryBackupAsync(sourcePath, settings.Database, request.DatabaseName, log, ct);
                if (!backedUp)
                    result.Warnings.Add("Database backup was skipped (see log for details).");
            }

            // --- 4. remove bitbucket-pipelines.yml ---
            log.Step("4/7 Removing bitbucket-pipelines.yml…");
            _cleaner.RemovePipelineFiles(targetPath, log);

            // --- 5. git history ---
            if (adoptExistingRepo)
            {
                log.Step("5/7 Committing into the existing repository…");
            }
            else
            {
                log.Step("5/7 Initializing fresh git history…");
                await _git.InitFreshAsync(targetPath, log, ct);
            }

            // Between init and add: the inherited .gitignore decides what the commit contains, and once
            // staging has run there is no way left to tell which files its rules swallowed.
            await _gitIgnore.NormalizeAsync(targetPath, log, ct);
            await _git.CommitAllAsync(targetPath, request.CommitMessage, log, ct);

            // --- 6. build gate ---
            if (request.RunBuilds)
            {
                log.Step("6/7 Building clone (React + .NET)…");
                var build = await _buildRunner.RunAsync(targetPath, log, ct);
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
                log.Success(adoptExistingRepo
                    ? "Dry run complete — the push to the existing repository was skipped."
                    : "Dry run complete — Bitbucket repo creation and push were skipped.");
                return result;
            }

            string pushTarget;
            string branch;
            IReadOnlyDictionary<string, string>? pushEnv = null;

            if (adoptExistingRepo)
            {
                log.Step("7/7 Pushing to the existing repository…");

                var origin = await _git.GetRemoteUrlAsync(targetPath, "origin", ct);
                if (string.IsNullOrWhiteSpace(origin))
                    return Fail(result, "The target repository has no 'origin' remote to push to.", log);

                branch = await _git.GetBranchAsync(targetPath, ct);
                result.RepositoryUrl = StripCredentials(origin);

                if (origin.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    pushTarget = BuildAuthenticatedUrl(origin, settings.Bitbucket);
                }
                else
                {
                    // An SSH remote authenticates through the configured key, exactly like the source pull.
                    pushTarget = "origin";
                    pushEnv = BuildGitEnv(settings);
                }

                log.Info($"Pushing {branch} to {result.RepositoryUrl}");
            }
            else
            {
                log.Step("7/7 Creating Bitbucket repo and pushing…");

                var slug = request.BitbucketRepoSlug ?? Slugify(targetNamespace);
                var repo = await _bitbucket.CreateRepositoryAsync(settings.Bitbucket, slug, log, ct);
                result.RepositoryUrl = repo.HtmlUrl;

                await _git.AddRemoteAsync(targetPath, "origin", repo.CloneUrl, log, ct);

                branch = "master";
                pushTarget = BuildAuthenticatedUrl(repo.CloneUrl, settings.Bitbucket);
            }

            var push = await _git.PushAsync(targetPath, pushTarget, branch, pushEnv, log, ct);
            if (!push.Success) return Fail(result, $"git push failed: {push.Combined}", log);

            result.Success = true;
            log.Success($"Done. Repository: {result.RepositoryUrl}");
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

    /// <summary>
    /// Builds the environment for git operations over SSH. Always relaxes first-connection host-key
    /// checking; when an SSH key path is configured, forces git to use exactly that key.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildGitEnv(AppSettings settings)
    {
        var ssh = "ssh -o StrictHostKeyChecking=accept-new";
        if (!string.IsNullOrWhiteSpace(settings.SshKeyPath))
        {
            // forward slashes are safe for ssh on all platforms
            var keyPath = PathUtil.Expand(settings.SshKeyPath).Replace('\\', '/');
            ssh += $" -i \"{keyPath}\" -o IdentitiesOnly=yes";
        }
        return new Dictionary<string, string> { ["GIT_SSH_COMMAND"] = ssh };
    }

    /// <summary>Builds a one-shot authenticated push URL so credentials are never stored in .git/config.</summary>
    private static string BuildAuthenticatedUrl(string cloneUrl, BitbucketSettings settings)
    {
        // With no credentials configured, git's own helper is a better bet than a "https://:@host" URL.
        if (string.IsNullOrEmpty(settings.Username) || string.IsNullOrEmpty(settings.AppPassword))
            return StripCredentials(cloneUrl);

        var uri = new Uri(cloneUrl);
        var user = Uri.EscapeDataString(settings.Username);
        var pass = Uri.EscapeDataString(settings.AppPassword);
        return $"{uri.Scheme}://{user}:{pass}@{uri.Host}{uri.PathAndQuery}";
    }

    /// <summary>Drops any userinfo from a URL — a clone URL may already carry it, and it must not be logged.</summary>
    private static string StripCredentials(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return url;

        return $"{uri.Scheme}://{uri.Host}{uri.PathAndQuery}";
    }

    /// <summary>Entries in the target that go beyond what a freshly created, cloned repository holds.</summary>
    private static IReadOnlyList<string> OccupyingEntries(string targetPath)
        => Directory.EnumerateFileSystemEntries(targetPath)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !RepositoryScaffold.Contains(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
