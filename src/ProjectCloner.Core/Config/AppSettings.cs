namespace ProjectCloner.Core.Config;

/// <summary>Persisted application settings (stored as JSON in the user profile).</summary>
public sealed class AppSettings
{
    public string SourceRootFolder { get; set; } = string.Empty;
    public string DefaultSourceNamespace { get; set; } = string.Empty;

    /// <summary>Optional path to a private SSH key used for git over SSH (e.g. the source pull). Empty = git defaults.</summary>
    public string SshKeyPath { get; set; } = string.Empty;

    public BitbucketSettings Bitbucket { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
}

public sealed class BitbucketSettings
{
    public string Workspace { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Bitbucket App Password or repository access token. Stored locally, never committed.</summary>
    public string AppPassword { get; set; } = string.Empty;

    public bool MakePrivate { get; set; } = true;

    /// <summary>Optional Bitbucket project key the new repo is created under.</summary>
    public string DefaultProjectKey { get; set; } = string.Empty;
}

public sealed class UpdateSettings
{
    public string GitHubOwner { get; set; } = string.Empty;
    public string GitHubRepo { get; set; } = string.Empty;
    public bool CheckOnStartup { get; set; } = true;
}

public sealed class DatabaseSettings
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public int Port { get; set; } = 3306;
    public string BackupFolder { get; set; } = string.Empty;

    /// <summary>Tables whose <i>data</i> is excluded from the dump (schema kept, rows dropped).</summary>
    public List<string> TablesToClear { get; set; } = new();
}
