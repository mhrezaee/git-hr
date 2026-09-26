using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GitHr.App.ViewModels;

namespace GitHr.App.Views;

public partial class CloneDialog : Window
{
    public CloneDialog()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            // A copied "https://github.com/..." URL is the common case: pre-fill it.
            try
            {
                if (Clipboard is { } clipboard && DataContext is CloneDialogViewModel vm)
                {
                    vm.SuggestUrl(await clipboard.TryGetTextAsync());
                }
            }
            catch (Exception)
            {
                // Clipboard access can fail (locked by another app); the URL box just stays empty.
            }
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    private CloneDialogViewModel ViewModel => (CloneDialogViewModel)DataContext!;

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(ViewModel.ParentFolder);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Clone into folder",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            ViewModel.ParentFolder = path;
        }
    }

    private void Clone_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.CanClone)
        {
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
