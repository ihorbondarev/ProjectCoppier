namespace ProjectCloner.Core.Models;

/// <summary>Everything the orchestrator needs to perform one clone.</summary>
public sealed class CloneRequest
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public required string SourceNamespace { get; init; }

    /// <summary>Target namespace. When null, the target folder name is used.</summary>
    public string? TargetNamespace { get; init; }

    /// <summary>When true, stop before creating the Bitbucket repo and pushing.</summary>
    public bool DryRun { get; init; }

    /// <summary>When true, run the React + .NET build gate before pushing.</summary>
    public bool RunBuilds { get; init; } = true;

    /// <summary>When true, attempt the optional MySQL backup step (non-fatal).</summary>
    public bool BackupDatabase { get; init; }

    /// <summary>Per-run database name to back up. When null/empty, the default from settings is used.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>Repository slug for the new Bitbucket repo. When null, derived from the target namespace.</summary>
    public string? BitbucketRepoSlug { get; init; }

    public string CommitMessage { get; init; } = "Initial commit";
}

/// <summary>Outcome of a clone run.</summary>
public sealed class CloneResult
{
    public bool Success { get; set; }
    public string? TargetPath { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? FailureReason { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>Result of the build gate.</summary>
public sealed class BuildResult
{
    public bool Success { get; set; } = true;
    public List<string> StepsRun { get; } = new();
    public string? FailedStep { get; set; }
}
