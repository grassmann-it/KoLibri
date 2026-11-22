using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KoLibri.Desktop;

/// <summary>
/// The one compact confirmation of a Mall purchase: item, shop, unit price, quantity (default 1), total and the
/// budget left, all from the row the user clicked (no round trip to KoL). The price, stock and budget are checked
/// once when Buy is pressed, against the offer read at that moment. Returns the quantity or null.
/// </summary>
public sealed class MallPurchaseDialog : Window
{
    private readonly MallPurchasePromptContext _context;
    private readonly NumericUpDown _quantity = new() { Minimum = 1, Value = 1, Width = 110, FormatString = "0" };
    private readonly TextBlock _total = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _problems = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
    private readonly Button _buy = new() { Content = "Buy", IsDefault = true };
    private readonly Button _cancel = new() { Content = "Cancel", IsCancel = true };

    public MallPurchaseDialog(MallPurchasePromptContext context)
    {
        _context = context;
        Title = "Confirm purchase";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _quantity.Maximum = Math.Max(1, context.MaxQuantity);
        _buy.Classes.Add("primary");
        _cancel.Classes.Add("secondary");

        var offer = context.Offer;
        var c = CultureInfo.InvariantCulture;
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock { Text = offer.ItemName, FontWeight = FontWeight.Bold, FontSize = 16 });
        header.Children.Add(new TextBlock { Text = $"{offer.ShopName} (#{offer.ShopId}) · {context.Account}", Opacity = 0.7 });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = string.Create(c, $"{offer.UnitPrice:N0} Meat each ×"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_quantity);

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(_total);
        if (context.RemainingDailyBudget >= 0)
            info.Children.Add(new TextBlock { Text = string.Create(c, $"Budget left today: {context.RemainingDailyBudget:N0} Meat"), Opacity = 0.7 });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_buy);

        Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { header, row, info, _problems, buttons } };

        _quantity.ValueChanged += (_, _) => Refresh();
        _buy.Click += (_, _) => Close((int?)Quantity);
        _cancel.Click += (_, _) => Close((int?)null);
        Opened += (_, _) => { Refresh(); _quantity.Focus(); };
    }

    private int Quantity => (int)Math.Max(1, _quantity.Value ?? 1);

    private void Refresh()
    {
        var total = _context.Offer.UnitPrice * Quantity;
        _total.Text = string.Create(CultureInfo.InvariantCulture, $"Total {total:N0} Meat");
        // Advisory only: the budget ledger decides when Buy is pressed.
        var over = _context.RemainingDailyBudget >= 0 && total > _context.RemainingDailyBudget;
        _problems.Text = over ? "This is above today's remaining budget; it will be refused." : string.Empty;
        _buy.IsEnabled = !over;
    }
}

/// <summary>Shows <see cref="MallPurchaseDialog"/> modally over the main window.</summary>
public sealed class WindowMallPurchasePrompt(Window owner) : IMallPurchasePrompt
{
    public Task<int?> AskAsync(MallPurchasePromptContext context, CancellationToken cancellationToken)
        => new MallPurchaseDialog(context).ShowDialog<int?>(owner);
}
