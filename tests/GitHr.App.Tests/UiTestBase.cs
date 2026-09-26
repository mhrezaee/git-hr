using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitHr.App.ViewModels;
using GitHr.App.Views;
using GitHr.Core;

namespace GitHr.App.Tests;

/// <summary>
/// Creates a temporary repository and opens it in the real main window, headlessly: input is
/// delivered to the window in memory, never to the desktop.
/// </summary>
public abstract class UiTestBase : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "githr-apptests", Guid.NewGuid().ToString("N"));
    protected readonly GitRunner Git = new();

    /// <summary>Answers for confirmation dialogs; tests can change it. Every question is recorded.</summary>
    protected bool ConfirmAnswer = true;
    protected string? PromptAnswer;
    protected readonly List<string> Questions = [];

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException) { }
    }

    protected async Task<(MainWindow Window, MainViewModel Vm)> OpenWindowAsync(Func<Task>? setup = null)
    {
        Directory.CreateDirectory(Dir);
        await RunGitAsync("init", "-b", "main");
        await RunGitAsync("config", "user.name", "Test");
        await RunGitAsync("config", "user.email", "test@example.com");
        await RunGitAsync("config", "commit.gpgsign", "false");
        await RunGitAsync("config", "core.autocrlf", "false");
        await CommitAsync("a.txt", "a\n", "Initial");
        await RunGitAsync("branch", "feature/login");
        if (setup is not null)
        {
            await setup();
        }

        var (window, vm) = CreateWindow();
        await vm.OpenRepositoryAsync(Dir);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasRepository, vm.ErrorMessage);
        return (window, vm);
    }

    /// <summary>The main window on its start screen, with dialogs answered by the test.</summary>
    protected (MainWindow Window, MainViewModel Vm) CreateWindow()
    {
        // Keep tests away from the user's real settings file.
        var vm = new MainViewModel(AppSettings.Load(Path.Combine(Path.GetTempPath(), "githr-apptests", Guid.NewGuid() + ".json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.Confirm = (title, message, _, _) =>
        {
            Questions.Add($"{title}: {message}");
            return Task.FromResult(ConfirmAnswer);
        };
        vm.Prompt = (title, _, _) =>
        {
            Questions.Add(title);
            return Task.FromResult(PromptAnswer);
        };
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    protected async Task CommitAsync(string path, string content, string message)
    {
        await WriteAsync(path, content);
        await RunGitAsync("add", "-A");
        await RunGitAsync("commit", "-m", message);
    }

    protected Task WriteAsync(string path, string content) => File.WriteAllTextAsync(Path.Combine(Dir, path), content);

    protected async Task<string> RunGitAsync(params string[] args) => (await Git.RunAsync(Dir, args)).EnsureSuccess().Output;

    protected static async Task WaitUntilAsync(Func<bool> condition, string? because = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because ?? "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks the center of a control with the (headless) mouse.</summary>
    protected static void Click(Window window, Control control, MouseButton button = MouseButton.Left)
    {
        Dispatcher.UIThread.RunJobs();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Control is not in the window.");
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Dispatcher.UIThread.RunJobs();
    }

    protected static T Find<T>(Visual root, Func<T, bool> predicate) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(predicate);

    /// <summary>A visible push button with this text (CheckBox/ToggleButton derive from Button, so they are skipped).</summary>
    protected static Button FindButton(Visual root, string content) =>
        Find<Button>(root, b => b is not Avalonia.Controls.Primitives.ToggleButton && b.IsEffectivelyVisible && Equals(b.Content, content));

    /// <summary>Right-clicks a control, then runs the context-menu item with the given header through its binding.</summary>
    protected static void RunContextMenuItem(Window window, Control target, string header)
    {
        Click(window, target, MouseButton.Right);
        var menu = target.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.ContextMenu).First(m => m is not null)!;
        Assert.True(menu.IsOpen, "Context menu did not open.");
        var item = menu.GetLogicalDescendants().OfType<MenuItem>().First(m => Equals(m.Header, header));
        Assert.True(item.Command?.CanExecute(item.CommandParameter), $"'{header}' is not executable.");
        menu.Close();
        item.Command!.Execute(item.CommandParameter);
    }

    protected static void SaveFrame(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        using var stream = File.Create(Path.Combine(Path.GetTempPath(), name));
        frame?.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
