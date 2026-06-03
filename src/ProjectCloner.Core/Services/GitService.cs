using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

/// <summary>Git operations implemented by shelling out to the system <c>git</c> CLI.</summary>
public sealed class GitService : IGitService
{
    private readonly ProcessRunner _runner;

    public GitService(ProcessRunner runner) => _runner = runner;

    private Task<ProcessResult> Git(string repoPath, IProgress<ProgressReport>? log, CancellationToken ct, params string[] args)
        => _runner.RunAsync("git", args, repoPath, onOutput: line => log.Info(line), cancellationToken: ct);

    private Task<ProcessResult> Git(string repoPath, IReadOnlyDictionary<string, string>? env, IProgress<ProgressReport>? log, CancellationToken ct, params string[] args)
        => _runner.RunAsync("git", args, repoPath, environment: env, onOutput: line => log.Info(line), cancellationToken: ct);

    public async Task<bool> IsRepositoryAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return false;
        var r = await _runner.RunAsync("git", ["rev-parse", "--is-inside-work-tree"], path, cancellationToken: ct);
        return r.Success && r.StdOut.Trim() == "true";
    }

    public async Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("git", ["status", "--porcelain"], repoPath, cancellationToken: ct);
        return r.Success && string.IsNullOrWhiteSpace(r.StdOut);
    }

    public async Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], repoPath, cancellationToken: ct);
        return r.Success ? r.StdOut.Trim() : string.Empty;
    }

    public Task<ProcessResult> CheckoutAsync(string repoPath, string branch, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
        => Git(repoPath, log, ct, "checkout", branch);

    public Task<ProcessResult> PullAsync(string repoPath, IReadOnlyDictionary<string, string>? env = null, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
        => Git(repoPath, env, log, ct, "pull", "--ff-only");

    public Task<ProcessResult> ResetHardAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
        => Git(repoPath, log, ct, "reset", "--hard");

    public Task<ProcessResult> CleanAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
        => Git(repoPath, log, ct, "clean", "-fdx");

    public async Task InitFreshAsync(string repoPath, string commitMessage, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        var gitPath = Path.Combine(repoPath, ".git");
        if (Directory.Exists(gitPath)) ForceDeleteDirectory(gitPath);
        else if (File.Exists(gitPath)) File.Delete(gitPath);

        var init = await Git(repoPath, log, ct, "init", "-b", "master");
        if (!init.Success) throw new InvalidOperationException($"git init failed: {init.Combined}");

        var add = await Git(repoPath, log, ct, "add", "-A");
        if (!add.Success) throw new InvalidOperationException($"git add failed: {add.Combined}");

        // Pass identity inline so the commit succeeds even when git user.* is not configured globally.
        var commit = await _runner.RunAsync("git",
            ["-c", "user.name=ProjectCloner", "-c", "user.email=projectcloner@local", "commit", "-m", commitMessage],
            repoPath, onOutput: line => log.Info(line), cancellationToken: ct);
        if (!commit.Success) throw new InvalidOperationException($"git commit failed: {commit.Combined}");
    }

    public Task<ProcessResult> AddRemoteAsync(string repoPath, string name, string url, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
        => Git(repoPath, log, ct, "remote", "add", name, url);

    public Task<ProcessResult> PushAsync(string repoPath, string urlOrRemote, string branch, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
        => Git(repoPath, log, ct, "push", urlOrRemote, $"{branch}:{branch}");

    private static void ForceDeleteDirectory(string path)
    {
        // .git pack/index files are often read-only (especially on Windows); clear attributes first.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch { /* best effort */ }
        }
        Directory.Delete(path, recursive: true);
    }
}
