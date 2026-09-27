using Avalonia.Controls;
using Avalonia.Interactivity;
using GitHr.App.ViewModels;

namespace GitHr.App.Views;

public partial class ConflictResolverWindow : Window
{
    public ConflictResolverWindow()
    {
        InitializeComponent();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConflictResolverViewModel { CanSave: true })
        {
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
