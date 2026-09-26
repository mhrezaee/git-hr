using Avalonia.Data.Converters;
using Avalonia.Media;
using GitHr.Core;

namespace GitHr.App;

public static class Converters
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3DC47E"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#E0A83D"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E5534B"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#16A9E0"));
    private static readonly IBrush Purple = new SolidColorBrush(Color.Parse("#A66BDB"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#5C6370"));

    public static readonly IValueConverter BoolToWeight =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);

    public static readonly IValueConverter IsPositive = new FuncValueConverter<int, bool>(n => n > 0);

    public static readonly IValueConverter IsEditableDiff =
        new FuncValueConverter<GitHr.App.ViewModels.DiffMode, bool>(m => m != GitHr.App.ViewModels.DiffMode.ReadOnly);

    public static readonly IValueConverter FileKindBrush = new FuncValueConverter<FileChangeKind, IBrush>(kind => kind switch
    {
        FileChangeKind.Added or FileChangeKind.Untracked => Green,
        FileChangeKind.Deleted => Red,
        FileChangeKind.Renamed or FileChangeKind.Copied => Blue,
        FileChangeKind.Conflicted => Purple,
        _ => Yellow,
    });

    public static readonly IValueConverter RefKindBrush = new FuncValueConverter<GitRefKind, IBrush>(kind => kind switch
    {
        GitRefKind.LocalBranch => Blue,
        GitRefKind.RemoteBranch => Purple,
        GitRefKind.Tag => Yellow,
        _ => Gray,
    });
}
