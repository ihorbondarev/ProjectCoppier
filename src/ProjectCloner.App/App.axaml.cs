using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProjectCloner.App.ViewModels;
using ProjectCloner.App.Views;
using ProjectCloner.Core.Config;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Services;
using ProjectCloner.Core.Update;

namespace ProjectCloner.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root — wire up the Core services.
            var runner = new ProcessRunner();
            var git = new GitService(runner);
            var backupService = new DatabaseBackupService(runner);
            var orchestrator = new CloneOrchestrator(
                git,
                new ProjectCopier(),
                new PipelineCleaner(),
                new BuildRunner(runner),
                new BitbucketClient(),
                backupService);
            var settingsStore = new SettingsStore();
            var updateService = new UpdateService();

            var viewModel = new MainWindowViewModel(orchestrator, backupService, settingsStore, updateService)
            {
                RequestShutdown = () => desktop.Shutdown()
            };
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
