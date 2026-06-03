using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProjectCloner.Core.Config;
using ProjectCloner.Core.Models;
using ProjectCloner.Core.Services;
using ProjectCloner.Core.Update;

namespace ProjectCloner.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly CloneOrchestrator _orchestrator;
    private readonly IDatabaseBackupService _backupService;
    private readonly SettingsStore _settingsStore;
    private readonly IUpdateService _updateService;
    private readonly IProgress<ProgressReport> _progress;

    private AppSettings _settings;
    private CancellationTokenSource? _cts;

    public MainWindowViewModel(CloneOrchestrator orchestrator, IDatabaseBackupService backupService,
        SettingsStore settingsStore, IUpdateService updateService)
    {
        _orchestrator = orchestrator;
        _backupService = backupService;
        _settingsStore = settingsStore;
        _updateService = updateService;
        _settings = settingsStore.Load();

        // Created on the UI thread, so reports marshal back to the UI thread automatically.
        _progress = new Progress<ProgressReport>(r => Log.Add(LogLine.From(r)));

        SourcePath = _settings.SourceRootFolder;
        SourceNamespace = _settings.DefaultSourceNamespace;
        DatabaseName = _settings.Database.DatabaseName; // default; overridable per run
    }

    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _targetPath = string.Empty;
    [ObservableProperty] private string _sourceNamespace = string.Empty;
    [ObservableProperty] private string _targetNamespace = string.Empty;
    [ObservableProperty] private bool _dryRun = true;
    [ObservableProperty] private bool _runBuilds = true;
    [ObservableProperty] private string _databaseName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackupDatabaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Ready";

    public ObservableCollection<LogLine> Log { get; } = new();

    /// <summary>Set by the composition root; invoked to close the app so a downloaded update can be applied.</summary>
    public Action? RequestShutdown { get; set; }

    /// <summary>Reloads settings after the settings window is saved.</summary>
    public void ReloadSettings()
    {
        _settings = _settingsStore.Load();
        if (string.IsNullOrEmpty(SourcePath)) SourcePath = _settings.SourceRootFolder;
        if (string.IsNullOrEmpty(SourceNamespace)) SourceNamespace = _settings.DefaultSourceNamespace;
        if (string.IsNullOrEmpty(DatabaseName)) DatabaseName = _settings.Database.DatabaseName;
    }

    public AppSettings CurrentSettings => _settings;

    private bool CanClone() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        Log.Clear();

        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(TargetPath))
        {
            _progress.Report(ProgressReport.Error("Source and target paths are required."));
            return;
        }

        var request = new CloneRequest
        {
            SourcePath = SourcePath.Trim(),
            TargetPath = TargetPath.Trim(),
            SourceNamespace = SourceNamespace.Trim(),
            TargetNamespace = string.IsNullOrWhiteSpace(TargetNamespace) ? null : TargetNamespace.Trim(),
            DryRun = DryRun,
            RunBuilds = RunBuilds
        };

        IsBusy = true;
        Status = "Working…";
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _orchestrator.RunAsync(request, _settings, _progress, _cts.Token);
            Status = result.Success
                ? (result.RepositoryUrl is { Length: > 0 } url ? $"Done → {url}" : "Done")
                : $"Failed: {result.FailureReason}";
        }
        catch (Exception ex)
        {
            _progress.Report(ProgressReport.Error(ex.Message));
            Status = "Failed";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        Status = "Cancelling…";
    }

    /// <summary>
    /// Standalone MySQL backup — independent of the clone flow. Lets you run it while connected to a
    /// VPN, separately from git operations (which run without the VPN).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task BackupDatabaseAsync()
    {
        Log.Clear();

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            _progress.Report(ProgressReport.Error("Source project path is required (its pipeline file holds the DB host)."));
            return;
        }

        IsBusy = true;
        Status = "Backing up database…";
        _cts = new CancellationTokenSource();
        try
        {
            var ok = await _backupService.TryBackupAsync(
                SourcePath.Trim(),
                _settings.Database,
                string.IsNullOrWhiteSpace(DatabaseName) ? null : DatabaseName.Trim(),
                _progress,
                _cts.Token);
            Status = ok ? "Database backup complete." : "Database backup skipped (see log).";
        }
        catch (Exception ex)
        {
            _progress.Report(ProgressReport.Error(ex.Message));
            Status = "Backup failed";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        _progress.Report(ProgressReport.Step("Checking for updates…"));
        try
        {
            var update = await _updateService.CheckForUpdateAsync(_settings.Update, _progress);
            if (update is null)
            {
                Status = "No updates available";
                return;
            }

            _progress.Report(ProgressReport.Success($"Update {update.Version} available — downloading…"));
            var applied = await _updateService.DownloadAndApplyAsync(update, _progress);
            if (applied)
            {
                Status = "Restarting to apply update…";
                RequestShutdown?.Invoke();
            }
            else
            {
                Status = $"Update {update.Version} downloaded";
            }
        }
        catch (Exception ex)
        {
            _progress.Report(ProgressReport.Error($"Update check failed: {ex.Message}"));
        }
    }

    /// <summary>Default the target path to a sibling of the source root using the namespace name.</summary>
    public string SuggestTargetPath(string folderName)
    {
        var root = string.IsNullOrWhiteSpace(_settings.SourceRootFolder)
            ? (string.IsNullOrWhiteSpace(SourcePath) ? Environment.CurrentDirectory : Path.GetDirectoryName(SourcePath) ?? Environment.CurrentDirectory)
            : _settings.SourceRootFolder;
        return Path.Combine(root, folderName);
    }
}
