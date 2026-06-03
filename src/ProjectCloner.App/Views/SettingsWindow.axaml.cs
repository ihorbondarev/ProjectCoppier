using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
