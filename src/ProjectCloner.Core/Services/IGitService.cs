using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

public interface IGitService
{
    Task<bool> IsRepositoryAsync(string path, CancellationToken ct = default);
    Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default);
    Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// The checked-out branch, resolved through the symbolic ref so it also works in a repository
    /// with no commits yet — which is exactly what cloning a freshly created Bitbucket repo gives you.
    /// </summary>
    Task<string> GetBranchAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// The branch the current one tracks (e.g. <c>origin/feature-x</c>), or null when it tracks nothing —
    /// a purely local branch, which there is no way and no need to pull.
    /// </summary>
    Task<string?> GetUpstreamAsync(string repoPath, CancellationToken ct = default);

    /// <summary>The configured URL of <paramref name="remote"/>, or null when it does not exist.</summary>
    Task<string?> GetRemoteUrlAsync(string repoPath, string remote, CancellationToken ct = default);

    Task<ProcessResult> CheckoutAsync(string repoPath, string branch, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
    Task<ProcessResult> PullAsync(string repoPath, IReadOnlyDictionary<string, string>? env = null, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
    Task<ProcessResult> ResetHardAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
    Task<ProcessResult> CleanAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    /// <summary>
    /// Removes the existing .git folder and creates a fresh, commit-less repo on master.
    /// Committing is a separate step so the clone's .gitignore can be normalized in between —
    /// once <c>git add</c> has run, an ignore rule that dropped a file can no longer be spotted.
    /// </summary>
    Task InitFreshAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    /// <summary>Stages everything and commits it, supplying an identity inline in case git has none configured.</summary>
    Task CommitAllAsync(string repoPath, string commitMessage, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    Task<ProcessResult> AddRemoteAsync(string repoPath, string name, string url, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    /// <summary><paramref name="urlOrRemote"/> may be a named remote or a full (optionally authenticated) URL.</summary>
    Task<ProcessResult> PushAsync(string repoPath, string urlOrRemote, string branch,
        IReadOnlyDictionary<string, string>? env = null, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
}
