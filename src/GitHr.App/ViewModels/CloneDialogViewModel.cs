using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHr.Core;

namespace GitHr.App.ViewModels;

/// <summary>Input for the clone dialog: URL, parent folder and folder name (derived from the URL until edited).</summary>
public partial class CloneDialogViewModel : ViewModelBase
{
    private bool _folderNameEdited;
    private bool _settingFolderName;

    public CloneDialogViewModel(string parentFolder)
    {
        ParentFolder = parentFolder;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(ValidationMessage), nameof(CanClone))]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(ValidationMessage), nameof(CanClone))]
    public partial string ParentFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(ValidationMessage), nameof(CanClone))]
    public partial string FolderName { get; set; } = "";

    public string TargetPath => ParentFolder.Trim().Length == 0 || FolderName.Trim().Length == 0
        ? ""
        : Path.Combine(ParentFolder.Trim(), FolderName.Trim());

    /// <summary>Why the clone can't start yet, or null when everything is valid.</summary>
    public string? ValidationMessage
    {
        get
        {
            if (Url.Trim().Length == 0) return "Enter the repository URL.";
            if (ParentFolder.Trim().Length == 0) return "Choose the folder to clone into.";
            if (FolderName.Trim().Length == 0 || FolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Enter a valid folder name.";
            try
            {
                if (Directory.Exists(TargetPath) && Directory.EnumerateFileSystemEntries(TargetPath).Any())
                {
                    return $"“{TargetPath}” already exists and is not empty.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"“{TargetPath}” can't be used: {ex.Message}";
            }
            return null;
        }
    }

    public bool CanClone => ValidationMessage is null;

    /// <summary>Pre-fills the URL from the clipboard when it looks like a git URL.</summary>
    public void SuggestUrl(string? clipboardText)
    {
        var text = clipboardText?.Trim();
        if (Url.Length == 0 && text is not null && !text.Contains('\n') && LooksLikeGitUrl(text))
        {
            Url = text;
        }
    }

    public static bool LooksLikeGitUrl(string text) =>
        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("git@", StringComparison.OrdinalIgnoreCase);

    partial void OnUrlChanged(string value)
    {
        if (!_folderNameEdited)
        {
            _settingFolderName = true;
            FolderName = GitUrl.GetRepositoryName(value) ?? "";
            _settingFolderName = false;
        }
    }

    partial void OnFolderNameChanged(string value)
    {
        if (!_settingFolderName)
        {
            // The user typed their own name: stop following the URL (unless they cleared it).
            _folderNameEdited = value.Length > 0;
        }
    }
}
