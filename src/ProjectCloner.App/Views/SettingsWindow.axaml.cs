using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ProjectCloner.App.ViewModels;
using ProjectCloner.Core.Config;

namespace ProjectCloner.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(new SettingsStore());
        DataContext = _viewModel;
    }

    private async void OnBrowseSshKey(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select your SSH private key",
            AllowMultiple = false
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
            _viewModel.SshKeyPath = path;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
