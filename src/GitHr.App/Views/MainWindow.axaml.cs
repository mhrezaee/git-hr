using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // Tunnel so the shortcut works even while a text box or list has focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.PickRepository = PickRepositoryAsync;
            vm.Confirm = (title, message, confirmText, destructive) => Dialogs.ConfirmAsync(this, title, message, confirmText, destructive);
            vm.Prompt = (title, message, initial) => Dialogs.PromptAsync(this, title, message, initial);
            vm.CopyText = async text =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            };
            vm.RequestClone = async input => await new CloneDialog { DataContext = input }.ShowDialog<bool?>(this) == true;
        }
    }

    private void DiffList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && DataContext is MainViewModel vm)
        {
            vm.SetDiffSelection(list.SelectedItems?.OfType<DiffLineViewModel>() ?? []);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            ViewModel.CloneCommand.Execute(null);
        }
        // Ctrl+P or Ctrl+Shift+P (Cmd on macOS) opens the command palette.
        else if (e.Key == Key.P && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            ViewModel.OpenPalette();
            Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
        }
        else if (e.Key == Key.Escape && ViewModel.Palette.IsOpen)
        {
            e.Handled = true;
            ViewModel.Palette.Close();
        }
    }

    private async void PaletteBox_KeyDown(object? sender, KeyEventArgs e)
    {
        var palette = ViewModel.Palette;
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                e.Handled = true;
                palette.MoveSelection(e.Key == Key.Down ? 1 : -1);
                if (palette.SelectedItem is { } selected)
                {
                    PaletteList.ScrollIntoView(selected);
                }
                break;
            case Key.Enter:
                e.Handled = true;
                await palette.ExecuteAsync();
                break;
        }
    }

    private async void PaletteList_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PaletteItem item)
        {
            await ViewModel.Palette.ExecuteAsync(item);
        }
    }

    private void PaletteBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => ViewModel.Palette.Close();

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
