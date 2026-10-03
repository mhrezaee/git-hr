using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitHr.App.Controls;
using GitHr.App.ViewModels;
using GitHr.App.Views;
using GitHr.Core.History;

namespace GitHr.App.Tests;

/// <summary>History search, file history and blame, driven through the real window.</summary>
public sealed class HistoryViewTests : UiTestBase
{
    private BlameViewModel? _blame;

    /// <summary>History: Initial, add Calc.cs, fix a bug (other author), rename to Calculator.cs, add docs.</summary>
    private async Task<(MainWindow Window, MainViewModel Vm)> OpenWithHistoryAsync()
    {
        var (window, vm) = await OpenWindowAsync(async () =>
        {
            await CommitAsync("Calc.cs", "class Calc\n{\n    int Add(int a, int b) => a - b;\n}\n", "Add calculator");
            await WriteAsync("Calc.cs", "class Calc\n{\n    int Add(int a, int b) => a + b;\n}\n");
            await RunGitAsync("add", "-A");
            await RunGitAsync("-c", "user.name=Grace Hopper", "-c", "user.email=grace@example.com", "commit", "--date=2030-01-01T12:00:00", "-m", "Fix addition bug");
            await RunGitAsync("mv", "Calc.cs", "Calculator.cs");
            await RunGitAsync("commit", "-m", "Rename Calc to Calculator");
            await CommitAsync("README.md", "# Docs\n", "Add docs");
        });
        vm.OpenBlame = blame =>
        {
            _blame = blame;
            return Task.CompletedTask;
        };
        return (window, vm);
    }

    [Fact]
    public Task SearchBox_FiltersHistory_AndEscapeShowsAllAgain() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithHistoryAsync();
        Assert.Equal(5, vm.Commits.Count);

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        var box = window.FindControl<TextBox>("HistorySearchBox")!;
        Assert.True(box.IsFocused, "Ctrl+F focuses the history search box.");
        window.KeyTextInput("BUG");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Commits.Count == 1, "search results");

        Assert.Equal("Fix addition bug", vm.Commits[0].Subject);
        Assert.Equal("Fix addition bug", vm.SelectedCommit?.Subject); // the first result opens right away
        Assert.Equal("Message contains “BUG” · 1 commit", vm.HistoryFilterTitle);
        SaveFrame(window, "githr-history-search.png");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Commits.Count == 5 && !vm.IsHistoryFiltered, "full history again");
        Assert.Equal("", vm.SearchText);
    });

    [Theory]
    [InlineData(HistorySearchKind.Author, "hopper", "Fix addition bug")]
    [InlineData(HistorySearchKind.Code, "a - b", "Add calculator")]
    public Task SearchKinds(HistorySearchKind kind, string text, string expected) => RunUi(async () =>
    {
        var (window, vm) = await OpenWithHistoryAsync();
        vm.SearchKind = kind;
        vm.SearchText = text;
        Click(window, FindButton(window, "Search"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.IsHistoryFiltered);

        // "a - b" was added by "Add calculator" and removed by the fix: pickaxe finds both.
        Assert.Contains(vm.Commits, c => c.Subject == expected);
    });

    [Fact]
    public Task SearchWithoutResults_SaysSo() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithHistoryAsync();
        vm.SearchText = "nothing like this";
        await vm.SearchHistoryCommand.ExecuteAsync(null);

        Assert.Empty(vm.Commits);
        Assert.EndsWith("no commits found", vm.HistoryFilterTitle);
        Click(window, FindButton(window, "Show all commits"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.Commits.Count == 5);
    });

    [Fact]
    public Task FileHistory_FromTheChangesList_FollowsTheRename_AndOpensTheFileDiff() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithHistoryAsync();
        await WriteAsync("Calculator.cs", "class Calculator\n{\n    int Add(int a, int b) => a + b;\n}\n");
        await vm.RefreshAsync();

        RunContextMenuItem(window, RowOf(window, vm.UnstagedFiles.Single()), "File history");
        await WaitUntilAsync(() => !vm.IsBusy && vm.IsHistoryFiltered, "file history");

        Assert.Equal(["Rename Calc to Calculator", "Fix addition bug", "Add calculator"], vm.Commits.Select(c => c.Subject));
        Assert.Equal("History of Calculator.cs · 3 commits", vm.HistoryFilterTitle);

        // Picking an older commit shows the file under its old name, with the diff already open.
        vm.SelectedCommit = vm.Commits.Single(c => c.Subject == "Fix addition bug");
        var fix = vm.SelectedCommit.Commit.ShortSha;
        await WaitUntilAsync(() => vm.SelectedCommitFile?.Path == "Calc.cs" && vm.DiffTitle == $"Calc.cs @ {fix}",
            "diff of Calc.cs in the fix");
        Assert.Contains(vm.DiffLines, l => l.Text == "+    int Add(int a, int b) => a + b;");
        SaveFrame(window, "githr-file-history.png");
    });

    [Fact]
    public Task FileHistory_SurvivesARefresh() => RunUi(async () =>
    {
        var (_, vm) = await OpenWithHistoryAsync();
        vm.SelectedCommit = vm.Commits.Single(c => c.Subject == "Add docs");
        await WaitUntilAsync(() => vm.SelectedCommitFiles.Count == 1);
        await vm.ShowFileHistoryCommand.ExecuteAsync(vm.SelectedCommitFiles.Single());
        Assert.Equal(["Add docs"], vm.Commits.Select(c => c.Subject));

        await vm.RefreshAsync(); // e.g. the window regained focus
        Assert.Equal(["Add docs"], vm.Commits.Select(c => c.Subject));
        Assert.True(vm.IsHistoryFiltered);
    });

    [Fact]
    public Task Blame_FromTheCommitFiles_ShowsWhoChangedEachLine_AndJumpsToTheCommit() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithHistoryAsync();
        vm.SelectedCommit = vm.Commits.Single(c => c.Subject == "Rename Calc to Calculator");
        await WaitUntilAsync(() => vm.SelectedCommitFiles.Count == 1);
        vm.SelectedTabIndex = 1;

        RunContextMenuItem(window, RowOf(window, vm.SelectedCommitFiles.Single()), "Blame at this commit…");
        await WaitUntilAsync(() => _blame is not null, "blame opened");

        var blame = _blame!;
        Assert.Equal(4, blame.Lines.Count);
        var fixedLine = blame.Lines.Single(l => l.Line.Text.Contains("a + b"));
        Assert.Equal("Grace Hopper", fixedLine.Author);
        Assert.Equal("Fix addition bug", fixedLine.CommitSummary);
        Assert.True(fixedLine.IsBlockStart);
        Assert.Equal("Add calculator", blame.Lines[0].CommitSummary);
        Assert.Equal("", blame.Lines[1].CommitSummary); // same commit as the line above: details only once
        Assert.Contains(fixedLine.Styled.Syntax, s => s.Length > 0); // C# is colored
        Assert.True(fixedLine.AgeOpacity > blame.Lines[0].AgeOpacity); // newer line, brighter bar

        // The blame window itself, rendered.
        var blameWindow = new BlameWindow { DataContext = blame };
        blameWindow.Show(window);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(blameWindow.GetVisualDescendants().OfType<DiffText>(), t => t.Styled?.Text.Contains("a + b") == true);
        SaveFrame(blameWindow, "githr-blame.png");
        blameWindow.Close();

        await blame.ShowCommitCommand.ExecuteAsync(fixedLine);
        Assert.Equal("Fix addition bug", vm.SelectedCommit?.Subject);
    });

    [Fact]
    public Task Blame_ShowCommit_FindsCommitsHiddenByAFilter() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithHistoryAsync();
        await WriteAsync("Calculator.cs", "class Calc\n{\n    int Add(int a, int b) => a + b; // checked\n}\n");
        await vm.RefreshAsync();
        RunContextMenuItem(window, RowOf(window, vm.UnstagedFiles.Single()), "Blame…");
        await WaitUntilAsync(() => _blame is not null);
        Assert.True(_blame!.Lines[2].IsUncommitted);

        vm.SearchText = "docs";
        await vm.SearchHistoryCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Commits, c => c.Subject == "Add calculator");

        await _blame.ShowCommitCommand.ExecuteAsync(_blame.Lines[0]);
        Assert.Equal("Add calculator", vm.SelectedCommit?.Subject);
        Assert.Equal(HistorySearchKind.Sha, vm.SearchKind);

        await _blame.ShowCommitCommand.ExecuteAsync(_blame.Lines[2]); // uncommitted: nothing to show
        Assert.Equal("Add calculator", vm.SelectedCommit?.Subject);
    });
}
