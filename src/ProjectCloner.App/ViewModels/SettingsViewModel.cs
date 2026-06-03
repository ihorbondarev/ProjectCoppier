using CommunityToolkit.Mvvm.ComponentModel;
using ProjectCloner.Core.Config;

namespace ProjectCloner.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;

    public SettingsViewModel(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();

        SourceRootFolder = _settings.SourceRootFolder;
        DefaultSourceNamespace = _settings.DefaultSourceNamespace;

        BitbucketWorkspace = _settings.Bitbucket.Workspace;
        BitbucketUsername = _settings.Bitbucket.Username;
        BitbucketAppPassword = _settings.Bitbucket.AppPassword;
        BitbucketProjectKey = _settings.Bitbucket.DefaultProjectKey;
        BitbucketPrivate = _settings.Bitbucket.MakePrivate;

        GitHubOwner = _settings.Update.GitHubOwner;
        GitHubRepo = _settings.Update.GitHubRepo;
        CheckUpdatesOnStartup = _settings.Update.CheckOnStartup;

        DbEnabled = _settings.Database.Enabled;
        DbUsername = _settings.Database.Username;
        DbPassword = _settings.Database.Password;
        DbName = _settings.Database.DatabaseName;
        DbPort = _settings.Database.Port;
        DbBackupFolder = _settings.Database.BackupFolder;
        DbTablesToClear = string.Join(", ", _settings.Database.TablesToClear);
    }

    [ObservableProperty] private string _sourceRootFolder = string.Empty;
    [ObservableProperty] private string _defaultSourceNamespace = string.Empty;

    [ObservableProperty] private string _bitbucketWorkspace = string.Empty;
    [ObservableProperty] private string _bitbucketUsername = string.Empty;
    [ObservableProperty] private string _bitbucketAppPassword = string.Empty;
    [ObservableProperty] private string _bitbucketProjectKey = string.Empty;
    [ObservableProperty] private bool _bitbucketPrivate = true;

    [ObservableProperty] private string _gitHubOwner = string.Empty;
    [ObservableProperty] private string _gitHubRepo = string.Empty;
    [ObservableProperty] private bool _checkUpdatesOnStartup = true;

    [ObservableProperty] private bool _dbEnabled;
    [ObservableProperty] private string _dbUsername = string.Empty;
    [ObservableProperty] private string _dbPassword = string.Empty;
    [ObservableProperty] private string _dbName = string.Empty;
    [ObservableProperty] private int _dbPort = 3306;
    [ObservableProperty] private string _dbBackupFolder = string.Empty;
    [ObservableProperty] private string _dbTablesToClear = string.Empty;

    public void Save()
    {
        _settings.SourceRootFolder = SourceRootFolder.Trim();
        _settings.DefaultSourceNamespace = DefaultSourceNamespace.Trim();

        _settings.Bitbucket.Workspace = BitbucketWorkspace.Trim();
        _settings.Bitbucket.Username = BitbucketUsername.Trim();
        _settings.Bitbucket.AppPassword = BitbucketAppPassword;
        _settings.Bitbucket.DefaultProjectKey = BitbucketProjectKey.Trim();
        _settings.Bitbucket.MakePrivate = BitbucketPrivate;

        _settings.Update.GitHubOwner = GitHubOwner.Trim();
        _settings.Update.GitHubRepo = GitHubRepo.Trim();
        _settings.Update.CheckOnStartup = CheckUpdatesOnStartup;

        _settings.Database.Enabled = DbEnabled;
        _settings.Database.Username = DbUsername.Trim();
        _settings.Database.Password = DbPassword;
        _settings.Database.DatabaseName = DbName.Trim();
        _settings.Database.Port = DbPort;
        _settings.Database.BackupFolder = DbBackupFolder.Trim();
        _settings.Database.TablesToClear = DbTablesToClear
            .Split(new[] { ',', ';', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .ToList();

        _store.Save(_settings);
    }
}
