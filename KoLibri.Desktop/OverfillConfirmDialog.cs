using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KoLibri.Desktop;

/// <summary>Small modal asking whether to go ahead; returns true only for the explicit confirm button.</summary>
public sealed class OverfillConfirmDialog : Window
{
    public OverfillConfirmDialog(string title, string message, string confirmLabel)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var confirm = new Button { Content = confirmLabel };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        confirm.Classes.Add("primary");
        cancel.Classes.Add("secondary");
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, confirm }
                }
            }
        };
    }

    public static async Task<bool> AskAsync(Window owner, string title, string message, string confirmLabel)
        => await new OverfillConfirmDialog(title, message, confirmLabel).ShowDialog<bool?>(owner) == true;
}
