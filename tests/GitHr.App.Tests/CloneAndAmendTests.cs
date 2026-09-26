using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GitHr.App.ViewModels;
using GitHr.App.Views;

namespace GitHr.App.Tests;

public sealed class CloneAndAmendTests : UiTestBase
{
    [Fact]
    public Task CloneButton_ShowsDialog_ClonesWithLiveProgress_AndOpensTheClone() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(); // Dir becomes the "remote" we clone from
        var clonesFolder = Dir + "-clones";
        CloneDialogViewModel? shown = null;
        vm.RequestClone = dialog =>
        {
            shown = dialog;
            dialog.Url = new Uri(Dir).AbsoluteUri; // file:// uses git's transport, so it reports progress
            dialog.ParentFolder = clonesFolder;
            return Task.FromResult(true);
        };
        var busyTexts = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.BusyText) && vm.BusyText is { } text)
            {
                busyTexts.Add(text);
            }
        };

        // The toolbar button (icon + label content), found by the command it is bound to.
        Click(window, Find<Button>(window, b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, vm.CloneCommand)));

        var expectedName = Path.GetFileName(Dir);
        await WaitUntilAsync(() => !vm.IsBusy && vm.RepositoryPath is { } p && p.StartsWith(clonesFolder, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expectedName, shown!.FolderName); // derived from the URL
        Assert.Equal(expectedName, vm.RepositoryName);
        Assert.Contains(busyTexts, t => t.StartsWith($"Cloning {expectedName}:") && t.Contains("objects", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.RemoteBranches, b => b.Name == "origin/main");
        TryDelete(clonesFolder);
    });

    [Fact]
    public Task CloneDialog_DerivesFolderName_UntilUserEditsIt() => RunUi(async () =>
    {
        var parent = Directory.CreateDirectory(Dir).FullName;
        var vm = new CloneDialogViewModel(parent);
        var dialog = new CloneDialog { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        var urlBox = dialog.FindControl<TextBox>("UrlBox")!;
        var cloneButton = dialog.FindControl<Button>("CloneButton")!;
        Assert.False(cloneButton.IsEnabled);
        Assert.Equal("Enter the repository URL.", vm.ValidationMessage);

        urlBox.Focus();
        dialog.KeyTextInput("https://github.com/owner/my-repo.git");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("my-repo", vm.FolderName);
        Assert.Equal(Path.Combine(parent, "my-repo"), vm.TargetPath);
        Assert.True(cloneButton.IsEnabled);
        SaveFrame(dialog, "githr-clone-dialog.png");

        // Once the user picks their own folder name, changing the URL no longer overwrites it.
        vm.FolderName = "custom";
        vm.Url = "git@github.com:owner/other.git";
        Assert.Equal("custom", vm.FolderName);

        // A non-empty target folder is rejected before git even runs.
        Directory.CreateDirectory(Path.Combine(parent, "custom"));
        await File.WriteAllTextAsync(Path.Combine(parent, "custom", "x.txt"), "x");
        vm.FolderName = "custom ";
        vm.FolderName = "custom";
        Assert.Contains("not empty", vm.ValidationMessage);
        Dispatcher.UIThread.RunJobs();
        Assert.False(cloneButton.IsEnabled);
        dialog.Close();
    });

    [Theory]
    [InlineData("https://github.com/a/b.git", "https://github.com/a/b.git")]
    [InlineData("git@github.com:a/b.git", "git@github.com:a/b.git")]
    [InlineData("hello world", "")]
    [InlineData("https://a/b\nsecond line", "")]
    public void CloneDialog_SuggestsOnlyGitUrlsFromClipboard(string clipboard, string expected)
    {
        var vm = new CloneDialogViewModel(Path.GetTempPath());
        vm.SuggestUrl(clipboard);
        Assert.Equal(expected, vm.Url);
    }

    [Theory]
    [InlineData("Receiving objects:  45% (450/1000), 1.20 MiB | 2.00 MiB/s", 45.0)]
    [InlineData("Resolving deltas: 100% (3/3), done.", 100.0)]
    [InlineData("Cloning into 'repo'...", null)]
    public void ParsePercent(string line, double? expected)
    {
        Assert.Equal(expected, MainViewModel.ParsePercent(line));
    }

    [Fact]
    public Task AmendCheckbox_PrefillsMessage_AndAmendsWithStagedFile() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        await WriteAsync("forgotten.txt", "oops\n");
        await RunGitAsync("add", "forgotten.txt");
        await vm.RefreshAsync();

        var amend = Find<CheckBox>(window, c => Equals(c.Content, "Amend last commit"));
        Assert.True(amend.IsEnabled);
        Click(window, amend);
        await WaitUntilAsync(() => vm.CommitMessage == "Initial", "last commit message prefilled");
        Assert.Equal("Amend last commit", vm.CommitButtonText);

        Click(window, FindButton(window, "Amend last commit"));
        await WaitUntilAsync(() => !vm.IsBusy && !vm.IsAmend);

        Assert.Equal("1", (await RunGitAsync("rev-list", "--count", "HEAD")).Trim()); // replaced, not added
        Assert.Contains("forgotten.txt", await RunGitAsync("show", "--name-only", "--format=", "HEAD"));
        Assert.Equal("", vm.CommitMessage);
        Assert.Empty(Questions); // not pushed, so no warning
    });

    [Fact]
    public Task Amend_RewordOnly_AndUncheckRestoresEmptyMessage() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();

        vm.IsAmend = true;
        await WaitUntilAsync(() => vm.CommitMessage == "Initial");
        vm.IsAmend = false;
        Assert.Equal("", vm.CommitMessage); // untouched prefill is removed again

        vm.IsAmend = true;
        await WaitUntilAsync(() => vm.CommitMessage == "Initial");
        vm.CommitMessage = "Initial commit, reworded";
        Click(window, FindButton(window, "Amend last commit"));
        await WaitUntilAsync(() => !vm.IsBusy && !vm.IsAmend);

        Assert.Equal("Initial commit, reworded", (await RunGitAsync("log", "-1", "--format=%s")).Trim());
    });

    private static void TryDelete(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
    }
}
