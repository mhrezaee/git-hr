using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GitHr.App.ViewModels;

namespace GitHr.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Activated += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.RefreshIfIdleAsync();
            }
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await PickRepositoryAsync();
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            await ViewModel.RefreshIfIdleAsync();
        }
    }

    private async void OpenRepository_Click(object? sender, RoutedEventArgs e) => await PickRepositoryAsync();

    private async System.Threading.Tasks.Task PickRepositoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Git repository",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            await ViewModel.OpenRepositoryAsync(path);
        }
    }

    private async void Branch_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: BranchItemViewModel branch })
        {
            await ViewModel.CheckoutCommand.ExecuteAsync(branch);
        }
    }

    private async void UnstagedFile_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: FileChangeItemViewModel file })
        {
            await ViewModel.StageFileCommand.ExecuteAsync(file);
        }
    }

    private async void StagedFile_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: FileChangeItemViewModel file })
        {
            await ViewModel.UnstageFileCommand.ExecuteAsync(file);
        }
    }
}
