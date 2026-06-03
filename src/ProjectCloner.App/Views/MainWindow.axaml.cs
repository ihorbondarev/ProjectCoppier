using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ProjectCloner.App.ViewModels;

namespace ProjectCloner.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnBrowseSource(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Select the source project folder");
        if (folder is not null && ViewModel is not null)
            ViewModel.SourcePath = folder;
    }

    private async void OnBrowseTarget(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Select the target parent folder");
        if (folder is not null && ViewModel is not null)
            ViewModel.TargetPath = folder;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var window = new SettingsWindow();
        await window.ShowDialog(this);
        ViewModel.ReloadSettings();
    }
}
