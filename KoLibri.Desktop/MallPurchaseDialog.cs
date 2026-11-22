using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KoLibri.Core.Markt;

namespace KoLibri.Desktop;

/// <summary>
/// The confirmation window for one Mall purchase. It shows account, shop, unit price, quantity,
/// total and the budget left, all taken from a preview; "Buy" is only enabled for a preview
/// without problems whose quantity matches the field. Nothing is sent from here.
/// </summary>
public sealed class MallPurchaseDialog : Window
{
    private readonly Func<int, CancellationToken, Task<DesktopMallPurchasePreview>> _preview;
    private readonly NumericUpDown _quantity = new() { Minimum = 1, Value = 1, Width = 110, FormatString = "0" };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _problems = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
    private readonly Button _check = new() { Content = "Check price" };
    private readonly Button _buy = new() { Content = "Buy", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel", IsCancel = true };
    private readonly CancellationTokenSource _closed = new();
    private DesktopMallPurchasePreview? _current;

    public MallPurchaseDialog(MallPurchasePromptContext context,
        Func<int, CancellationToken, Task<DesktopMallPurchasePreview>> preview)
    {
        _preview = preview;
        Title = "Confirm purchase";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _quantity.Maximum = Math.Max(1, context.MaxQuantity);

        var offer = context.Offer;
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock { Text = offer.ItemName, FontWeight = FontWeight.Bold, FontSize = 16 });
        header.Children.Add(new TextBlock { Text = $"Account {context.Account} · {offer.ShopName} (#{offer.ShopId})", Opacity = 0.7 });

        var qtyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        qtyRow.Children.Add(new TextBlock { Text = "Quantity", VerticalAlignment = VerticalAlignment.Center });
        qtyRow.Children.Add(_quantity);
        qtyRow.Children.Add(_check);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_buy);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children = { header, qtyRow, _details, _problems, buttons }
        };

        _check.Click += async (_, _) => await CheckAsync();
        _quantity.ValueChanged += (_, _) => { _current = null; _buy.IsEnabled = false; };
        _buy.Click += (_, _) => Close(_current);
        _cancel.Click += (_, _) => Close(null);
        Opened += async (_, _) => await CheckAsync();
        Closed += (_, _) => _closed.Cancel();
    }

    private int Quantity => (int)Math.Max(1, _quantity.Value ?? 1);

    private async Task CheckAsync()
    {
        _buy.IsEnabled = false;
        _check.IsEnabled = false;
        _current = null;
        _problems.Text = string.Empty;
        _details.Text = "Reading the current offer…";
        try
        {
            var quantity = Quantity;
            var preview = await _preview(quantity, _closed.Token);
            if (_closed.IsCancellationRequested || quantity != Quantity) return;
            _details.Text = Describe(preview);
            _problems.Text = string.Join(Environment.NewLine, preview.Problems.Select(p => $"Blocked: {p}")
                .Concat(preview.Messages));
            _current = preview;
            _buy.IsEnabled = preview.CanConfirm;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _details.Text = string.Empty;
            _problems.Text = $"Could not read the offer: {exception.Message}";
        }
        finally
        {
            _check.IsEnabled = true;
        }
    }

    internal static string Describe(DesktopMallPurchasePreview p)
    {
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            FormattableString.Invariant($"Unit price: {p.UnitPrice:N0} meat") + (p.PriceChanged ? FormattableString.Invariant($" (listed {p.RequestedUnitPrice:N0})") : ""),
            string.Create(c, $"Quantity: {p.Quantity:N0}"),
            string.Create(c, $"Total: {p.TotalPrice:N0} meat (you have {p.Meat:N0})"),
            string.Create(c, $"Budget left today: {p.RemainingDailyBudget:N0} meat · per purchase after this: {p.RemainingPurchaseBudget:N0}")
        };
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Shows <see cref="MallPurchaseDialog"/> modally over the main window.</summary>
public sealed class WindowMallPurchasePrompt(Window owner) : IMallPurchasePrompt
{
    public Task<DesktopMallPurchasePreview?> AskAsync(
        MallPurchasePromptContext context,
        Func<int, CancellationToken, Task<DesktopMallPurchasePreview>> preview,
        CancellationToken cancellationToken)
        => new MallPurchaseDialog(context, preview).ShowDialog<DesktopMallPurchasePreview?>(owner);
}
