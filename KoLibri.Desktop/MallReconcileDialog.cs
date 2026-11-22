using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KoLibri.Desktop;

/// <summary>
/// Shows the unclear Meat amount and fresh account evidence; the user explicitly says whether the purchase
/// happened. Closing the window or Cancel changes nothing.
/// </summary>
public sealed class MallReconcileDialog : Window
{
    public MallReconcileDialog(string account, long amount, string evidence)
    {
        Title = "Reconcile unclear purchases";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var spent = new Button { Content = "It was bought: count as spent" };
        var notSpent = new Button { Content = "Not bought: release" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        spent.Classes.Add("primary");
        notSpent.Classes.Add("secondary");
        cancel.Classes.Add("secondary");
        spent.Click += (_, _) => Close(MallUnclearDecision.WasSpent);
        notSpent.Click += (_, _) => Close(MallUnclearDecision.NotSpent);
        cancel.Click += (_, _) => Close(MallUnclearDecision.Cancel);
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = string.Create(CultureInfo.InvariantCulture,
                        $"{account}: {amount:N0} Meat of today's purchases are counted as unclear against the daily budget.")
                },
                new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = evidence, Opacity = 0.8 },
                new TextBlock
                {
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = "Check the item in your inventory first. Counting it as spent keeps it in today's budget; releasing it frees the budget again."
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, notSpent, spent }
                }
            }
        };
    }
}

public sealed class WindowMallReconcilePrompt(Window owner) : IMallReconcilePrompt
{
    public async Task<MallUnclearDecision> AskAsync(string account, long amount, string evidence, CancellationToken cancellationToken)
        => await new MallReconcileDialog(account, amount, evidence).ShowDialog<MallUnclearDecision?>(owner)
           ?? MallUnclearDecision.Cancel;
}
