using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

public interface IGitService
{
    Task<bool> IsRepositoryAsync(string path, CancellationToken ct = default);
    Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default);
    Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default);

    Task<ProcessResult> CheckoutAsync(string repoPath, string branch, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
    Task<ProcessResult> PullAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
    Task<ProcessResult> ResetHardAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
    Task<ProcessResult> CleanAsync(string repoPath, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    /// <summary>Removes the existing .git folder and creates a fresh repo with a single initial commit on master.</summary>
    Task InitFreshAsync(string repoPath, string commitMessage, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    Task<ProcessResult> AddRemoteAsync(string repoPath, string name, string url, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    /// <summary><paramref name="urlOrRemote"/> may be a named remote or a full (optionally authenticated) URL.</summary>
    Task<ProcessResult> PushAsync(string repoPath, string urlOrRemote, string branch, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
}
