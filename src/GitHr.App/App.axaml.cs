using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GitHr.App.ViewModels;
using GitHr.App.Views;

namespace GitHr.App;

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
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            // "GitHr.App <path>" opens that repository directly.
            if (desktop.Args is [var path, ..])
            {
                _ = viewModel.OpenRepositoryAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}