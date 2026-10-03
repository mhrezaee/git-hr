using Avalonia.Controls;
using Avalonia.Input;
using GitHr.App.ViewModels;

namespace GitHr.App.Views;

public partial class BlameWindow : Window
{
    public BlameWindow()
    {
        InitializeComponent();
    }

    private async void BlameList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: BlameLineViewModel line } && DataContext is BlameViewModel blame)
        {
            await blame.ShowCommitCommand.ExecuteAsync(line);
        }
    }
}
