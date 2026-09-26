using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitHr.App.Views;

/// <summary>Small modal dialogs: confirmation and single-line text input.</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText, bool destructive)
    {
        var dialog = Create(title, message, initialText: null, confirmText, destructive, out _);
        return await dialog.ShowDialog<bool?>(owner) == true;
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initialText)
    {
        var dialog = Create(title, message, initialText, "OK", destructive: false, out var input);
        return await dialog.ShowDialog<bool?>(owner) == true ? input!.Text : null;
    }

    private static Window Create(string title, string message, string? initialText, string confirmText, bool destructive, out TextBox? input)
    {
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var box = initialText is null ? null : new TextBox { Text = initialText };
        var confirm = new Button { Content = confirmText, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        confirm.Classes.Add(destructive ? "danger" : "accent");
        // Destructive actions are never triggered by just pressing Enter.
        confirm.IsDefault = !destructive;
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        confirm.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (box is not null)
        {
            panel.Children.Add(box);
        }
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm },
        });
        window.Content = panel;

        window.Opened += (_, _) =>
        {
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
            }
            else
            {
                (destructive ? cancel : confirm).Focus();
            }
        };

        input = box;
        return window;
    }
}
